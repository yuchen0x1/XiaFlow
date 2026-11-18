using System;
using System.IO;
using System.Linq;
using XiaFlow.Core;
using XiaFlow.Core.Engines;
using Xunit;

namespace XiaFlow.Tests
{
    public class FormatRegistryTests
    {
        [Theory]
        [InlineData("a.zip", ArchiveFormat.Zip)]
        [InlineData("b.7z", ArchiveFormat.SevenZ)]
        [InlineData("c.rar", ArchiveFormat.Rar)]
        [InlineData("d.tar.gz", ArchiveFormat.GZip)]
        [InlineData("e.tar.bz2", ArchiveFormat.BZip2)]
        [InlineData("f.txz", ArchiveFormat.XZ)]
        [InlineData("g.tzst", ArchiveFormat.Zstd)]
        [InlineData("h.lz4", ArchiveFormat.Lz4)]
        [InlineData("i.iso", ArchiveFormat.Iso)]
        [InlineData("j.wim", ArchiveFormat.Wim)]
        [InlineData("k.cab", ArchiveFormat.Cab)]
        [InlineData("l.cpio", ArchiveFormat.Cpio)]
        [InlineData("m.rpm", ArchiveFormat.Rpm)]
        [InlineData("n.deb", ArchiveFormat.Deb)]
        [InlineData("o.001", ArchiveFormat.Split)]
        [InlineData("p.zip.001", ArchiveFormat.Split)]
        public void Extension_Detection(string file, ArchiveFormat expected)
        {
            var info = FormatRegistry.FindByExtension(file);
            Assert.NotNull(info);
            Assert.Equal(expected, info.Format);
        }

        [Fact]
        public void Unknown_Extension_Returns_Null()
        {
            Assert.Null(FormatRegistry.FindByExtension("file.xyz123"));
        }

        [Fact]
        public void Magic_Detection_Zip_7z_Gzip_Xz_Lz4()
        {
            var ws = TestEnv.NewWorkspace();
            try
            {
                byte[][] sigs =
                {
                    new byte[] { 0x50, 0x4B, 0x03, 0x04 },                      // zip
                    new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },          // 7z
                    new byte[] { 0x1F, 0x8B, 0x08, 0x00 },                      // gzip
                    new byte[] { 0xFD, 0x37, 0x7A, 0X58, 0x5A, 0x00 },          // xz
                    new byte[] { 0x04, 0x22, 0x4D, 0x18 },                      // lz4
                    new byte[] { 0x28, 0xB5, 0x2F, 0xFD },                      // zstd
                };
                ArchiveFormat[] expected = { ArchiveFormat.Zip, ArchiveFormat.SevenZ, ArchiveFormat.GZip, ArchiveFormat.XZ, ArchiveFormat.Lz4, ArchiveFormat.Zstd };
                for (int i = 0; i < sigs.Length; i++)
                {
                    var p = Path.Combine(ws, "t" + i + ".bin");
                    File.WriteAllBytes(p, sigs[i].Concat(new byte[16]).ToArray());
                    using var fs = File.OpenRead(p);
                    Assert.Equal(expected[i], FormatRegistry.DetectByMagic(fs));
                }
            }
            finally { TestEnv.Cleanup(ws); }
        }

        [Theory]
        [InlineData("arc.7z.001", true)]
        [InlineData("arc.7z.002", true)]
        [InlineData("movie.part1.rar", true)]
        [InlineData("plain.zip", false)]
        [InlineData("data.7z", false)]
        public void Split_Detection(string name, bool expected)
        {
            Assert.Equal(expected, VolumeHelper.IsSplitArchive(name));
        }
    }
}
