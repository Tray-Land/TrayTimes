using System.Buffers.Binary;
using TrayTimes.Tracking;

namespace TrayTimes.Tests.Tracking;

[TestClass]
public sealed class EtaIconTests
{
    private const uint Background = 0xFF005FB8;
    private const uint Foreground = 0xFFFFFFFF;

    [TestMethod]
    [DataRow(16)]
    [DataRow(24)]
    [DataRow(32)]
    public void Create_AnySize_WritesSingleImageIconHeader(int size)
    {
        // Act
        byte[] ico = EtaIcon.Create(7, size, Background, Foreground);

        // Assert
        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(ico));
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));
        Assert.AreEqual(size, ico[6]);
        Assert.AreEqual(size, ico[7]);
        Assert.AreEqual(32, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(12)));
        Assert.AreEqual(ico.Length - EtaIcon.ImageOffset, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(14)));
        Assert.AreEqual(EtaIcon.ImageOffset, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(18)));
    }

    [TestMethod]
    public void Create_Size16_WritesBitmapHeaderWithDoubledHeight()
    {
        // Act
        byte[] ico = EtaIcon.Create(7, 16, Background, Foreground);
        ReadOnlySpan<byte> header = ico.AsSpan(EtaIcon.ImageOffset);

        // Assert
        Assert.AreEqual(40, BinaryPrimitives.ReadInt32LittleEndian(header));
        Assert.AreEqual(16, BinaryPrimitives.ReadInt32LittleEndian(header[4..]));
        Assert.AreEqual(32, BinaryPrimitives.ReadInt32LittleEndian(header[8..]));
        Assert.AreEqual(EtaIcon.ImageOffset + 40 + (16 * 16 * 4) + (4 * 16), ico.Length);
    }

    [TestMethod]
    public void Create_CornerPixel_IsTransparent()
    {
        // Act
        byte[] ico = EtaIcon.Create(7, 32, Background, Foreground);

        // Assert
        Assert.AreEqual(0u, Pixel(ico, 32, 0, 0) >> 24);
    }

    [TestMethod]
    public void Create_EdgeMidpoint_IsOpaqueBackground()
    {
        // Act
        byte[] ico = EtaIcon.Create(7, 32, Background, Foreground);

        // Assert
        Assert.AreEqual(Background, Pixel(ico, 32, 0, 16));
    }

    [TestMethod]
    public void Create_EightCenter_IsForeground()
    {
        // Arrange: "8" is fully lit in its middle row, which is centered in the icon.
        const int size = 32;

        // Act
        byte[] ico = EtaIcon.Create(8, size, Background, Foreground);

        // Assert
        Assert.AreEqual(Foreground, Pixel(ico, size, size / 2, size / 2));
    }

    [TestMethod]
    public void Create_ZeroCenter_IsBackground()
    {
        // Arrange: "0" is hollow in the middle.
        const int size = 32;

        // Act
        byte[] ico = EtaIcon.Create(0, size, Background, Foreground);

        // Assert
        Assert.AreEqual(Background, Pixel(ico, size, size / 2, size / 2));
    }

    [TestMethod]
    public void Create_TwoDigitsDifferFromOneDigit()
    {
        // Act
        byte[] one = EtaIcon.Create(7, 16, Background, Foreground);
        byte[] two = EtaIcon.Create(17, 16, Background, Foreground);

        // Assert
        CollectionAssert.AreNotEqual(one, two);
    }

    [TestMethod]
    public void Create_OverNinetyNine_DrawsNinetyNine()
    {
        // Act
        byte[] capped = EtaIcon.Create(150, 16, Background, Foreground);
        byte[] ninetyNine = EtaIcon.Create(99, 16, Background, Foreground);

        // Assert
        CollectionAssert.AreEqual(ninetyNine, capped);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(256)]
    public void Create_SizeOutOfRange_Throws(int size)
    {
        // Act & Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EtaIcon.Create(1, size, Background, Foreground));
    }

    /// <summary>Reads pixel (x, y), top-down, back out of the bottom-up BGRA bitmap as 0xAARRGGBB.</summary>
    private static uint Pixel(byte[] ico, int size, int x, int y)
    {
        int bits = EtaIcon.ImageOffset + 40;
        return BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(bits + ((((size - 1 - y) * size) + x) * 4)));
    }
}
