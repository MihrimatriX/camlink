using System.IO.MemoryMappedFiles;

namespace CamLink.Desktop.Core;

/// <summary>
/// Paylaşılan bellek düzeni (sürüm 2); desktop/vcam/src/frame_shm.h ile birebir aynı bayt konumları.
/// Kapasite 4K NV12; sürüm 1 (1080p kapasiteli) bellek bundan küçüktür ve eski kamera sürücüsünden gelir.
/// </summary>
public static class Vcam
{
    public const string MapName = @"Global\CamLinkVcam";
    public const uint Magic = 0x43564C43; // "CLVC"
    public const int Header = 64, Capacity = 3840 * 2160 * 3 / 2, Total = Header + Capacity;
}

/// <summary>
/// NV12 karelerini sanal kamera DLL'inin paylaşılan belleğine yazar (seqlock: tek seq = yazılıyor).
/// Bellek, kamerayı bir uygulama açınca DLL tarafından oluşturulur; bu yüzden açma yavaşça yeniden denenir
/// ve bellek yokken kare atlanır (hata sayılmaz, <see cref="WriteNv12"/> true döner).
/// </summary>
public sealed unsafe class FrameBus : IFrameSink, IDisposable
{
    const long RetryMs = 500;       // açma denemeleri arası
    const long FreshMs = 2000;      // DLL'in consumerTick'i bundan eskiyse kimse kamerayı izlemiyor
    const long RequestFreshMs = 3000;   // req* yalnız tüketici tick'i bundan yeniyse geçerli (Çerçeve Sunucusu çökünce eski değer kalır)
    const int OffWidth = 8, OffHeight = 12, OffFps = 16, OffSeq = 24, OffWriterTick = 32, OffConsumerTick = 40;
    const int OffReqW = 48, OffReqH = 52, OffReqFps = 56;   // tüketicinin istediği mod; DLL Start'ta yazar, Stop'ta sıfırlar

    readonly string _name;
    // Açma, kapama, dispose ve işaretçi kullanımı bu kilit altında: dispose yazarken eşleme kalkarsa süreç çöker.
    // ponytail: kare başına kilit (kopya ~1 ms); durum yoklayıcısı en kötü bir kare süresi bekler.
    readonly object _gate = new();
    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    byte* _base;
    long _nextTry = long.MinValue;
    bool _disposed;
    volatile int _fps = 30;
    volatile string? _lastOpenError;
    volatile bool _needsUpdate;

    public FrameBus(string mapName = Vcam.MapName) => _name = mapName;

    /// <summary>Kareler başlığa bu fps ile yazılır. DLL geometriyi yalnız 1..120 iken kabul eder; dışı kırpılır.</summary>
    public int Fps { get => _fps; set => _fps = Math.Clamp(value, 1, 120); }

    /// <summary>
    /// Son açma denemesi "bellek yok"tan başka bir nedenle (erişim reddi, küçük bölüm...) başarısız olduysa iletisi;
    /// bellek henüz yoksa ya da açıldıysa null. Bellek yok = kimse kamerayı açmamış, hata değil.
    /// </summary>
    public string? LastOpenError => _lastOpenError;

    /// <summary>
    /// Son açma denemesi v2 kapasitesinden küçük bir bellek buldu: kamerayı eski sürüm sürücü yönetiyor ("kamera
    /// güncellemesi gerekli"). Bu bellekte kare yazılmaz; yeni sürücü kurulup bellek değişince kendiliğinden kalkar.
    /// </summary>
    public bool NeedsUpdate => _needsUpdate;

    /// <summary>Bellek açık mı. Kapalıysa en sık 500 ms'de bir yeniden dener; asla fırlatmaz.</summary>
    public bool IsOpen { get { lock (_gate) return TryOpenLocked(); } }

    /// <summary>DLL son 2 saniyede bir kare istedi mi (bir uygulama kamerayı izliyor).</summary>
    public bool HasConsumer
    {
        get
        {
            lock (_gate)
                return TryOpenLocked() && Environment.TickCount64 - Volatile.Read(ref *(long*)(_base + OffConsumerTick)) < FreshMs;
        }
    }

    /// <summary>
    /// false yalnız gerçek hata: bozuk boyut (sıfır, negatif, tek), kısa dizi ya da kare belleğe sığmıyor.
    /// Bellek henüz yoksa ya da DLL başlığı bitirmediyse kare atlanır ve true döner (kimse kamerayı açmamıştır).
    /// Bellek eski sürümse (<see cref="NeedsUpdate"/>) kare yazılamaz: false.
    /// </summary>
    public bool WriteNv12(ReadOnlySpan<byte> nv12, int width, int height)
    {
        if (width <= 0 || height <= 0 || ((width | height) & 1) != 0) return false;
        var px = (long)width * height;                       // en çok 2^62: taşmaz
        if (px > Vcam.Capacity * 2L / 3) return false;       // 3/2 çarpımından ÖNCE sığma denetimi (taşma bunu aşardı)
        var need = (int)(px * 3 / 2);
        if (nv12.Length < need) return false;

        lock (_gate)
        {
            if (!TryOpenLocked()) return !_needsUpdate;   // yoklama beklerken de eski belleği "sorun yok" sayma
            try
            {
                if (Volatile.Read(ref *(uint*)_base) != Vcam.Magic) return true;
                Interlocked.Increment(ref *(long*)(_base + OffSeq));   // tek: DLL bu kareyi kullanmaz
                *(uint*)(_base + OffWidth) = (uint)width;              // boyut tek penceresinde: eski kareyle eşleşmez
                *(uint*)(_base + OffHeight) = (uint)height;
                *(uint*)(_base + OffFps) = (uint)_fps;
                nv12[..need].CopyTo(new Span<byte>(_base + Vcam.Header, need));
                Interlocked.Increment(ref *(long*)(_base + OffSeq));   // çift: hazır
                Volatile.Write(ref *(long*)(_base + OffWriterTick), Environment.TickCount64);
                return true;
            }
            catch (Exception e) when (e is ObjectDisposedException or IOException)
            {
                CloseLocked();   // eşleme bozuldu: bu kare kaybolur, sonra yeniden açılır
                return true;
            }
        }
    }

    /// <summary>
    /// Windows uygulamasının sanal kameradan istediği mod (DLL'in başlığa yazdığı <c>reqW/reqH/reqFps</c>).
    /// Tüketici yoksa üçü de 0'dır; DLL'in tüketici tick'i 3 sn'den eskiyse (kimse kare istemiyor, çöken Çerçeve Sunucusu
    /// eski değerleri bırakmış olabilir) kayıtlı değerler yok sayılır ve 0 döner.
    /// false: bellek açık değil, eski sürüm ya da başlık henüz hazır değil (değerler 0).
    /// </summary>
    public bool TryReadRequest(out int width, out int height, out int fps)
    {
        width = height = fps = 0;
        lock (_gate)
        {
            if (!TryOpenLocked() || Volatile.Read(ref *(uint*)_base) != Vcam.Magic) return false;
            if (Environment.TickCount64 - Volatile.Read(ref *(long*)(_base + OffConsumerTick)) >= RequestFreshMs) return true;
            width = (int)Math.Min(Volatile.Read(ref *(uint*)(_base + OffReqW)), int.MaxValue);
            height = (int)Math.Min(Volatile.Read(ref *(uint*)(_base + OffReqH)), int.MaxValue);
            fps = (int)Math.Min(Volatile.Read(ref *(uint*)(_base + OffReqFps)), int.MaxValue);
            return true;
        }
    }

    bool TryOpenLocked()
    {
        if (_base != null) return true;
        var now = Environment.TickCount64;
        if (_disposed || now < _nextTry || !OperatingSystem.IsWindows()) return false;   // adlandırılmış Global bellek yalnız Windows'ta
        _nextTry = now + RetryMs;

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;
        byte* p = null;
        var tooSmall = false;
        try
        {
            // Bölümün DACL'i Interactive Users'a yalnız okuma/yazma verir: varsayılan ReadWrite uyar, FullControl istenmez.
            mmf = MemoryMappedFile.OpenExisting(_name);
            view = mmf.CreateViewAccessor();
            if (view.Capacity < Vcam.Total)
            {
                tooSmall = true;   // sürüm 1 düzeni: 4K kare sığmaz, istek alanları da yok
                throw new IOException("Bellek beklenenden küçük.");
            }
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            // Önceki yazar kare ortasında çöktüyse seq tektir; bir kez artırıp paritesini düzelt.
            if ((Volatile.Read(ref *(long*)(p + OffSeq)) & 1) != 0) Interlocked.Increment(ref *(long*)(p + OffSeq));
            _mmf = mmf;
            _view = view;
            _base = p;
            _lastOpenError = null;
            _needsUpdate = false;
            return true;
        }
        catch (Exception e)   // yok, erişim reddi, küçük bölüm: hepsi "henüz açılamadı"; sonra yine denenir
        {
            // Küçük bölüm hata değil, "kamera güncellemesi gerekli" durumudur (arayüzde ayrı uyarısı var).
            _needsUpdate = tooSmall;
            _lastOpenError = tooSmall || e is FileNotFoundException ? null : "Sanal kamera görüntü alamıyor: " + e.Message;
            if (p != null) view!.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose();
            mmf?.Dispose();
            return false;
        }
    }

    void CloseLocked()
    {
        if (_base != null) _view!.SafeMemoryMappedViewHandle.ReleasePointer();
        _base = null;
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _nextTry = Environment.TickCount64 + RetryMs;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseLocked();
        }
    }
}
