using CamLink.Desktop.Core;

namespace CamLink.Desktop.Core.Tests;

public class AdbTests
{
    [Fact] public void Adb_parse_handles_states_and_noise()   // İnceleme odağı 5
    {
        var o = "* daemon not running; starting now at tcp:5037\n* daemon started successfully\nList of devices attached\n340YC20G8T04Y4\tdevice\nemulator-5554\toffline\nABC\tunauthorized\n\n";
        var d = Adb.ParseDevices(o);
        Assert.Equal(3, d.Count);
        Assert.Contains(("340YC20G8T04Y4", "device"), d);
        Assert.Contains(("ABC", "unauthorized"), d);
        Assert.Empty(Adb.ParseDevices(""));
    }

    [Fact] public void Adb_parse_handles_crlf()
    {
        var o = "List of devices attached\r\n340YC20G8T04Y4\tdevice\r\nABC\tunauthorized\r\n\r\n";
        Assert.Equal([("340YC20G8T04Y4", "device"), ("ABC", "unauthorized")], Adb.ParseDevices(o));
    }

    [Fact] public void Adb_parse_handles_long_listing_and_spaces()   // adb devices -l
    {
        var o = "List of devices attached\n"
              + "340YC20G8T04Y4         device usb:1-2 product:beyond1 model:SM_G973F device:beyond1 transport_id:3\n"
              + "192.168.1.20:5555      offline product:x model:Y\n";
        Assert.Equal([("340YC20G8T04Y4", "device"), ("192.168.1.20:5555", "offline")], Adb.ParseDevices(o));
    }

    // Arayüz yalnız "device" durumuna göre davranır; diğer durumlar olduğu gibi aktarılır.
    [Fact] public void Adb_parse_passes_through_recovery_sideload_and_unauthorized()
    {
        var o = "List of devices attached\nAAA\trecovery\nBBB\tsideload\nCCC\tunauthorized\nDDD\tbootloader\nEEE\tdevice\n";
        Assert.Equal(
            [("AAA", "recovery"), ("BBB", "sideload"), ("CCC", "unauthorized"), ("DDD", "bootloader"), ("EEE", "device")],
            Adb.ParseDevices(o));
    }

    // Linux'ta udev kuralı yoksa adb seri yerine '?' yazar ve durum serbest metindir; ilk sözcük durum olur ("no"), "device" değildir.
    [Fact] public void Adb_parse_keeps_first_word_of_a_linux_no_permissions_row()
    {
        var o = "List of devices attached\n????????????\tno permissions (user in plugdev group; are your udev rules wrong?); see [http://developer.android.com/tools/device.html]\n";
        var d = Adb.ParseDevices(o);
        Assert.Equal([("????????????", "no")], d);
        Assert.DoesNotContain(d, x => x.State == "device");
    }

    [Fact] public void Adb_parse_ignores_everything_without_a_device_list()
    {
        Assert.Empty(Adb.ParseDevices("List of devices attached\n\n"));
        Assert.Empty(Adb.ParseDevices("* daemon started successfully\n"));
        Assert.Empty(Adb.ParseDevices("error: no devices/emulators found\n"));
    }

    [Fact] public void Reverse_args_name_the_serial_and_port()
    {
        Assert.Equal(["-s", "ABC", "reverse", "tcp:4468", "tcp:4468"], Adb.ReverseArgs("ABC", 4468));
    }
}
