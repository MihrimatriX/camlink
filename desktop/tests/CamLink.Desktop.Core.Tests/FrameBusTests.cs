using System.IO.MemoryMappedFiles;
using CamLink.Desktop.Core;

namespace CamLink.Desktop.Core.Tests;

public class FrameBusTests
{
    // DLL'in oluşturduğu başlığı taklit eder: magic + version (2).
    static (MemoryMappedFile mmf, string name) MakeMap(long seq = 0)
    {
        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        var mmf = MemoryMappedFile.CreateNew(name, Vcam.Total);
        using var v = mmf.CreateViewAccessor();
        v.Write(0, Vcam.Magic);
        v.Write(4, 2u);
        if (seq != 0) v.Write(24, seq);
        return (mmf, name);
    }

    [Fact] public void Frame_is_written_with_even_seq()
    {
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name);
        var nv12 = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray(); // 4x2
        Assert.True(bus.WriteNv12(nv12, 4, 2));
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(4u, v.ReadUInt32(8));
        Assert.Equal(2u, v.ReadUInt32(12));
        Assert.Equal(30u, v.ReadUInt32(16));
        Assert.Equal(2L, v.ReadInt64(24));
        Assert.True(v.ReadInt64(32) > 0);
        var back = new byte[12]; v.ReadArray(Vcam.Header, back, 0, 12);
        Assert.Equal(nv12, back);
    }

    [Fact] public void Fps_goes_into_header()
    {
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name) { Fps = 60 };
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(60u, v.ReadUInt32(16));
    }

    [Fact] public void Oversized_frame_is_rejected()   // İnceleme odağı 4
    {
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name);
        Assert.False(bus.WriteNv12(new byte[4096 * 2160 * 3 / 2], 4096, 2160));   // 4K'dan büyük: sığmaz
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(0L, v.ReadInt64(24));
    }

    [Fact] public void Full_4k_frame_fits_the_v2_capacity()
    {
        Assert.Equal(3840 * 2160 * 3 / 2, Vcam.Capacity);
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name);
        var frame = new byte[3840 * 2160 * 3 / 2];
        frame[^1] = 7;
        Assert.True(bus.WriteNv12(frame, 3840, 2160));
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(2L, v.ReadInt64(24));
        Assert.Equal(3840u, v.ReadUInt32(8));
        Assert.Equal(2160u, v.ReadUInt32(12));
        Assert.Equal((byte)7, v.ReadByte(Vcam.Header + frame.Length - 1));
    }

    [Fact] public void Request_is_read_from_offsets_48_52_56()
    {
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));   // açılışı tetikler
        Assert.True(bus.TryReadRequest(out var w, out var h, out var fps));
        Assert.Equal((0, 0, 0), (w, h, fps));             // tüketici yok: DLL sıfırlamış
        using var v = mmf.CreateViewAccessor();
        v.Write(40, Environment.TickCount64);              // DLL canlı bir tüketiciyi yazar
        v.Write(48, 3840u);
        v.Write(52, 2160u);
        v.Write(56, 60u);
        Assert.True(bus.TryReadRequest(out w, out h, out fps));
        Assert.Equal((3840, 2160, 60), (w, h, fps));
        v.Write(48, 0u); v.Write(52, 0u); v.Write(56, 0u);   // DLL Stop'ta sıfırlar
        Assert.True(bus.TryReadRequest(out w, out h, out fps));
        Assert.Equal((0, 0, 0), (w, h, fps));
    }

    [Fact] public void Request_is_ignored_when_the_consumer_tick_is_stale()   // Çerçeve Sunucusu çökünce req* eski kalabilir
    {
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        using var v = mmf.CreateViewAccessor();
        v.Write(48, 3840u); v.Write(52, 2160u); v.Write(56, 60u);

        v.Write(40, 0L);                                                // tick hiç yazılmamış
        Assert.True(bus.TryReadRequest(out var w, out var h, out var fps));
        Assert.Equal((0, 0, 0), (w, h, fps));

        v.Write(40, Environment.TickCount64 - 5_000);                   // 3 sn'den eski
        Assert.True(bus.TryReadRequest(out w, out h, out fps));
        Assert.Equal((0, 0, 0), (w, h, fps));

        v.Write(40, Environment.TickCount64 - 2_000);                   // 3 sn içinde
        Assert.True(bus.TryReadRequest(out w, out h, out fps));
        Assert.Equal((3840, 2160, 60), (w, h, fps));

        v.Write(40, Environment.TickCount64);                           // tüketici döndü
        Assert.True(bus.TryReadRequest(out w, out h, out fps));
        Assert.Equal((3840, 2160, 60), (w, h, fps));
    }

    [Fact] public void Request_is_unavailable_without_a_usable_map()
    {
        using var none = new FrameBus($"Local\\yok-{Guid.NewGuid():N}");
        Assert.False(none.TryReadRequest(out var w, out var h, out var fps));
        Assert.Equal((0, 0, 0), (w, h, fps));

        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        using var mmf = MemoryMappedFile.CreateNew(name, Vcam.Total);   // başlık henüz yazılmadı
        using (var v = mmf.CreateViewAccessor()) { v.Write(48, 1920u); v.Write(52, 1080u); v.Write(56, 30u); }
        using var bus = new FrameBus(name);
        Assert.False(bus.TryReadRequest(out w, out h, out fps));   // magic yok
        Assert.Equal((0, 0, 0), (w, h, fps));
    }

    [Fact] public void V1_sized_map_means_camera_update_needed_not_an_exception()   // İnceleme odağı 4
    {
        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        using var mmf = MemoryMappedFile.CreateNew(name, 64 + 1920 * 1080 * 3 / 2);   // sürüm 1 düzeni
        using (var v = mmf.CreateViewAccessor()) { v.Write(0, Vcam.Magic); v.Write(4, 1u); }
        using var bus = new FrameBus(name);
        Assert.False(bus.NeedsUpdate);
        var ex = Record.Exception(() => Assert.False(bus.WriteNv12(new byte[12], 4, 2)));
        Assert.Null(ex);
        Assert.True(bus.NeedsUpdate);
        Assert.False(bus.IsOpen);
        Assert.Null(bus.LastOpenError);                  // ayrı bir "görüntü alamıyor" uyarısı çıkmaz; güncelleme uyarısı yeter
        Assert.False(bus.WriteNv12(new byte[12], 4, 2)); // yeniden denemeyi beklerken de false
        Assert.False(bus.TryReadRequest(out _, out _, out _));
        using var view = mmf.CreateViewAccessor();
        Assert.Equal(0L, view.ReadInt64(24));            // eski belleğe hiçbir şey yazılmadı
    }

    [Fact] public void Update_flag_clears_once_a_v2_map_appears_in_its_place()
    {
        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        using var bus = new FrameBus(name);
        var old = MemoryMappedFile.CreateNew(name, 64 + 1920 * 1080 * 3 / 2);
        Assert.False(bus.WriteNv12(new byte[12], 4, 2));
        Assert.True(bus.NeedsUpdate);
        old.Dispose();   // eski DLL kaldırıldı; ad serbest
        using var mmf = MemoryMappedFile.CreateNew(name, Vcam.Total);
        using var v = mmf.CreateViewAccessor();
        v.Write(0, Vcam.Magic);
        Assert.True(SpinWait.SpinUntil(() => bus.IsOpen, TimeSpan.FromSeconds(5)));   // yoklama 500 ms'de bir
        Assert.False(bus.NeedsUpdate);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
    }

    [Fact] public void Update_flag_clears_when_the_old_map_disappears()
    {
        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        using var bus = new FrameBus(name);
        var old = MemoryMappedFile.CreateNew(name, 64 + 1920 * 1080 * 3 / 2);
        Assert.False(bus.WriteNv12(new byte[12], 4, 2));
        Assert.True(bus.NeedsUpdate);
        old.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !bus.IsOpen && !bus.NeedsUpdate, TimeSpan.FromSeconds(5)));   // yoklama 500 ms'de bir
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));   // bellek yok: atlanır, hata değil
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4, 0)]
    [InlineData(-4, 2)]
    [InlineData(4, -2)]
    [InlineData(3, 2)]     // tek genişlik
    [InlineData(4, 3)]     // tek yükseklik
    [InlineData(65536, 65536)] // int taşması: 65536*65536 = 0
    [InlineData(2_000_000_000, 2_000_000_000)]          // long'ta 3/2 çarpımı taşar
    [InlineData(int.MaxValue - 1, int.MaxValue - 1)]    // en büyük çift boyut
    public void Bad_dimensions_are_rejected(int w, int h)
    {
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name);
        Assert.False(bus.WriteNv12(new byte[64], w, h));
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(0L, v.ReadInt64(24));
        Assert.Equal(0u, v.ReadUInt32(8));
    }

    [Fact] public void Short_span_is_rejected()
    {
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name);
        Assert.False(bus.WriteNv12(new byte[11], 4, 2));   // 12 bayt gerekir
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(0L, v.ReadInt64(24));
    }

    [Fact] public void Consumer_is_seen_from_recent_tick()
    {
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));   // açılışı tetikler
        Assert.False(bus.HasConsumer);
        using (var v = mmf.CreateViewAccessor()) v.Write(40, Environment.TickCount64);
        Assert.True(bus.HasConsumer);
        using (var v = mmf.CreateViewAccessor()) v.Write(40, Environment.TickCount64 - 2500);
        Assert.False(bus.HasConsumer);
    }

    [Fact] public void Missing_map_is_not_open_and_write_is_skipped()
    {
        using var bus = new FrameBus($"Local\\yok-{Guid.NewGuid():N}");
        Assert.False(bus.IsOpen);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        Assert.False(bus.HasConsumer);
        Assert.Null(bus.LastOpenError);   // "henüz yok" hata değildir
    }

    [Fact] public void Odd_seq_from_a_crashed_writer_is_repaired_on_open()
    {
        var (mmf, name) = MakeMap(seq: 1);
        using var bus = new FrameBus(name);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(4L, v.ReadInt64(24));   // 1 → 2 (açılışta), sonra +2
    }

    [Fact] public void Header_without_magic_skips_the_frame()
    {
        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        using var mmf = MemoryMappedFile.CreateNew(name, Vcam.Total);   // DLL başlığı henüz yazmadı
        using var bus = new FrameBus(name);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        Assert.True(bus.IsOpen);
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(0L, v.ReadInt64(24));
        Assert.Equal(0u, v.ReadUInt32(8));
        v.Write(0, Vcam.Magic);   // DLL başlığı bitirdi
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        Assert.Equal(2L, v.ReadInt64(24));
    }

    [Fact] public void Too_small_map_is_never_opened()
    {
        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        using var mmf = MemoryMappedFile.CreateNew(name, 4096);
        using (var v = mmf.CreateViewAccessor()) v.Write(0, Vcam.Magic);
        using var bus = new FrameBus(name);
        Assert.False(bus.WriteNv12(new byte[12], 4, 2));
        Assert.False(bus.IsOpen);
        Assert.True(bus.NeedsUpdate);   // v2 kapasitesinden küçük her bellek eski sürücüdendir; görünür olmalı
    }

    [Fact] public void Map_created_later_is_picked_up_by_polling()
    {
        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        using var bus = new FrameBus(name);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));   // yok: atlanır
        Assert.False(bus.IsOpen);
        using var mmf = MemoryMappedFile.CreateNew(name, Vcam.Total);
        using var v = mmf.CreateViewAccessor();
        v.Write(0, Vcam.Magic);
        Assert.True(SpinWait.SpinUntil(() => bus.IsOpen, TimeSpan.FromSeconds(5)));   // yoklama 500 ms'de bir
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        Assert.Equal(2L, v.ReadInt64(24));
    }

    [Fact] public void Open_error_is_cleared_after_a_successful_open()
    {
        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        using var bus = new FrameBus(name);
        var denied = MemoryMappedFile.CreateNew(name, Vcam.Total, MemoryMappedFileAccess.Read);   // erişim reddi
        Assert.False(bus.IsOpen);
        Assert.NotNull(bus.LastOpenError);
        denied.Dispose();   // ad serbest kalır
        using var mmf = MemoryMappedFile.CreateNew(name, Vcam.Total);
        Assert.True(SpinWait.SpinUntil(() => bus.IsOpen, TimeSpan.FromSeconds(5)));
        Assert.Null(bus.LastOpenError);
    }

    [Fact] public void Read_only_map_is_an_open_error_not_a_crash()   // erişim reddi görünür olmalı
    {
        var name = $"Local\\CamLinkTest-{Guid.NewGuid():N}";
        using var mmf = MemoryMappedFile.CreateNew(name, Vcam.Total, MemoryMappedFileAccess.Read);
        using var bus = new FrameBus(name);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        Assert.False(bus.IsOpen);
        Assert.NotNull(bus.LastOpenError);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(30, 30)]
    [InlineData(120, 120)]
    [InlineData(500, 120)]   // DLL geometriyi yalnız fps 1..120 iken kabul eder
    public void Fps_is_clamped_to_what_the_dll_accepts(int set, uint written)
    {
        var (mmf, name) = MakeMap();
        using var bus = new FrameBus(name) { Fps = set };
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(written, v.ReadUInt32(16));
        Assert.Equal((int)written, bus.Fps);
    }

    // DLL'in okuyucu algoritması: seq oku, tekse atla, kopyala, seq değişmediyse kabul et.
    // Yazar her kareyi tek bir bayt değeriyle doldurur; kabul edilen karede iki değer karışmamalı.
    [Fact] public void Seqlock_reader_never_accepts_a_torn_frame()
    {
        const int W = 64, H = 32, N = W * H * 3 / 2;
        var (mmf, name) = MakeMap();
        using var _ = mmf;
        using var bus = new FrameBus(name);
        var frames = new byte[256][];
        for (var k = 0; k < 256; k++) { frames[k] = new byte[N]; Array.Fill(frames[k], (byte)k); }
        Assert.True(bus.WriteNv12(frames[0], W, H));   // açılış

        using var view = mmf.CreateViewAccessor();
        var stop = false;
        long accepted = 0, torn = 0;
        var reader = new Thread(() =>
        {
            var copy = new byte[N];
            while (!Volatile.Read(ref stop))
            {
                var s1 = view.ReadInt64(24);
                Interlocked.MemoryBarrier();
                if ((s1 & 1) != 0) continue;
                view.ReadArray(Vcam.Header, copy, 0, N);
                Interlocked.MemoryBarrier();
                if (view.ReadInt64(24) != s1) continue;
                accepted++;
                if (copy.AsSpan().IndexOfAnyExcept(copy[0]) >= 0) torn++;
            }
        });
        try
        {
            reader.Start();
            var failed = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var k = 1; sw.ElapsedMilliseconds < 500; k++)
                if (!bus.WriteNv12(frames[k & 0xFF], W, H)) failed = true;
            Volatile.Write(ref stop, true);
            Assert.True(reader.Join(TimeSpan.FromSeconds(10)));
            Assert.False(failed);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            reader.Join(TimeSpan.FromSeconds(10));
        }
        Assert.True(accepted > 0);
        Assert.Equal(0L, torn);
    }

    [Fact] public void Dispose_is_idempotent_and_later_writes_are_skipped()
    {
        var (mmf, name) = MakeMap();
        var bus = new FrameBus(name);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        bus.Dispose();
        bus.Dispose();
        Assert.False(bus.IsOpen);
        Assert.False(bus.HasConsumer);
        Assert.True(bus.WriteNv12(new byte[12], 4, 2));
        using var v = mmf.CreateViewAccessor();
        Assert.Equal(2L, v.ReadInt64(24));
    }

    [Fact] public void Dispose_racing_writes_does_not_crash()
    {
        var (mmf, name) = MakeMap();
        using var _ = mmf;
        var bus = new FrameBus(name);
        var frame = new byte[1920 * 1080 * 3 / 2];
        using var go = new ManualResetEventSlim();
        Exception? failure = null;
        var writer = new Thread(() =>
        {
            go.Set();
            try { for (var i = 0; i < 200; i++) bus.WriteNv12(frame, 1920, 1080); }
            catch (Exception e) { failure = e; }
        });
        writer.Start();
        go.Wait();
        Thread.Sleep(5);
        bus.Dispose();
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
    }
}
