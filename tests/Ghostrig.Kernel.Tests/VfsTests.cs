using FsCheck;
using FsCheck.Xunit;
using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Time;
using Ghostrig.Kernel.Vfs;

namespace Ghostrig.Kernel.Tests;

public class VfsTests
{
    private static readonly Principal Chen = new("v.chen", PrivilegeTier.User);
    private static readonly Principal Guest = Principal.Anonymous;

    private static VirtualFileSystem NewFs()
    {
        var fs = new VirtualFileSystem(new SimClock(), VirtualFileSystem.DefaultRootAcl);
        fs.CreateDirectory("/srv/ftp/storefront", Principal.System, Acl.ForUser("v.chen", Permissions.All), createParents: true);
        fs.CreateDirectory("/.root_archive", Principal.System, Acl.RootOnly);
        fs.Write("/.root_archive/manifest.txt", Principal.System, FileContent.FromText("BIO-ASSET-09"));
        return fs;
    }

    [Theory]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("//srv///www/./public/", "/srv/www/public")]
    [InlineData("/", "/")]
    [InlineData("/x/..", "/")]
    public void Paths_normalize(string input, string expected) => Assert.Equal(expected, VfsPath.Parse(input).Value);

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/..")]
    [InlineData("/a/../../b")]
    [InlineData("/bad\\seg")]
    public void Malformed_paths_are_rejected(string input) =>
        Assert.Throws<InvalidVfsPathException>(() => VfsPath.Parse(input));

    [Property(MaxTest = 300)]
    public Property Normalization_is_idempotent(NonEmptyString raw)
    {
        var candidate = "/" + raw.Get.Replace('\\', '_');
        if (!VfsPath.TryParse(candidate, out var once))
        {
            return true.ToProperty();
        }

        return (VfsPath.Parse(once.Value) == once).Label(candidate);
    }

    [Fact]
    public void Ancestors_enumerate_from_root()
    {
        Assert.Equal(["/", "/a", "/a/b"], VfsPath.Parse("/a/b/c").Ancestors().Select(p => p.Value));
    }

    [Fact]
    public void Root_only_directories_deny_lower_tiers_with_403()
    {
        var fs = NewFs();
        var ex = Assert.Throws<AccessDeniedException>(() => fs.Read("/.root_archive/manifest.txt", Chen));
        Assert.Equal("403", ex.Code);
        Assert.Equal("BIO-ASSET-09", fs.ReadText("/.root_archive/manifest.txt", new Principal("root", PrivilegeTier.Root)));
    }

    [Fact]
    public void Hidden_entries_are_only_listed_on_request()
    {
        var fs = NewFs();
        Assert.DoesNotContain(fs.List("/", Guest), e => e.Name == ".root_archive");
        Assert.Contains(fs.List("/", Guest, includeHidden: true), e => e.Name == ".root_archive");
    }

    [Fact]
    public void Named_users_can_write_their_directories_but_guests_cannot()
    {
        var fs = NewFs();
        fs.Write("/srv/ftp/storefront/notes.txt", Chen, FileContent.FromText("hi"));
        Assert.Throws<AccessDeniedException>(() => fs.Write("/srv/ftp/storefront/x.txt", Guest, FileContent.FromText("x")));
        Assert.Equal("v.chen", fs.Stat("/srv/ftp/storefront/notes.txt", Chen).Acl.Owner);
    }

    [Theory]
    [InlineData("/home/kael/readme")]
    [InlineData("/home/kael/tool.exe")]
    [InlineData("/home/kael/driver.cs")]
    public void Files_outside_the_six_types_are_rejected(string path)
    {
        var fs = NewFs();
        Assert.Throws<InvalidVfsPathException>(() => fs.Write(path, Principal.System, FileContent.Empty));
    }

    [Theory]
    [InlineData("/a.txt", FileType.PlainText)]
    [InlineData("/a.enc", FileType.Encrypted)]
    [InlineData("/sys/drivers/net/NetCardDriver.drv", FileType.CScript)]
    [InlineData("/srv/www/public/index.cform", FileType.CScript)]
    [InlineData("/frame.png", FileType.Image)]
    [InlineData("/call.wav", FileType.Audio)]
    [InlineData("/vex_wetware_kickbacks.vid", FileType.Video)]
    public void Files_classify_into_the_six_types(string path, FileType expected) =>
        Assert.Equal(expected, FileTypes.FromPath(path));

    [Fact]
    public void Signed_baselines_detect_modification_and_restore()
    {
        var fs = new VirtualFileSystem(new SimClock(), VirtualFileSystem.DefaultRootAcl);
        var kael = new Principal("kael", PrivilegeTier.Root);
        const string driver = "/sys/drivers/net/NetCardDriver.drv";
        fs.CreateDirectory("/sys/drivers/net", Principal.System, createParents: true);
        fs.Write(driver, Principal.System, FileContent.FromText("stock"));
        fs.SignBaseline(driver, Principal.System);
        Assert.Equal(SignatureStatus.StockSigned, fs.Stat(driver, kael).Signature);

        fs.Write(driver, kael, FileContent.FromText("batched"));
        Assert.Equal(SignatureStatus.PlayerModified, fs.Stat(driver, kael).Signature);

        fs.RestoreBaseline(driver, kael);
        Assert.Equal("stock", fs.ReadText(driver, kael));
        Assert.Equal(SignatureStatus.StockSigned, fs.Stat(driver, kael).Signature);
    }

    [Fact]
    public void Deleting_a_non_empty_directory_requires_recursion()
    {
        var fs = NewFs();
        var ex = Assert.Throws<VfsTypeMismatchException>(() => fs.Delete("/.root_archive", Principal.System));
        Assert.Equal("ENOTEMPTY", ex.Code);
        fs.Delete("/.root_archive", Principal.System, recursive: true);
        Assert.False(fs.Exists("/.root_archive"));
    }

    [Property(MaxTest = 100)]
    public Property Synthetic_content_is_deterministic_and_random_access_consistent(ulong seed, PositiveInt offsetSeed)
    {
        var content = new SyntheticFileContent(1 << 16, seed, SyntheticAlphabet.PrintableLines);
        var offset = offsetSeed.Get % (1 << 16);
        var whole = content.ReadAll();
        var slice = new byte[Math.Min(97, (1 << 16) - offset)];
        content.ReadAt(offset, slice);

        var again = new SyntheticFileContent(1 << 16, seed, SyntheticAlphabet.PrintableLines);
        return (slice.AsSpan().SequenceEqual(whole.AsSpan(offset, slice.Length))
                && again.ContentHash == content.ContentHash
                && whole[31] == (byte)'\n').ToProperty();
    }

    [Fact]
    public void Huge_synthetic_files_are_never_materialized()
    {
        var dictionary = new SyntheticFileContent(4_000_000_000, 7, SyntheticAlphabet.PrintableLines);
        Assert.Throws<InvalidOperationException>(() => dictionary.ReadAll());
        var tail = new byte[32];
        Assert.Equal(32, dictionary.ReadAt(4_000_000_000 - 32, tail));
    }
}
