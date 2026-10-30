using System.Buffers.Binary;
using System.Text;
using ResoDrive.Core.Validation;

namespace ResoDrive.Core.Tests;

public sealed class SftpHostKeyValidatorTests
{
    // A public Ed25519 key, encoded in the OpenSSH wire format. No private key is used.
    private const string EncodedPublicKey = "AAAAC3NzaC1lZDI1NTE5AAAAINdamAGCsQq31Uv+08lkBzoO4XLz2qYjJa8CGmj3B1Ea";
    private const string PublicKey = "ssh-ed25519 " + EncodedPublicKey;

    [Fact]
    public void AcceptsARealOpenSshPublicKey() => Assert.True(SftpHostKeyValidator.IsValid(PublicKey));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ssh-ed25519 not-base64")]
    [InlineData("ssh-ed25519 AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("ssh-rsa " + EncodedPublicKey)]
    [InlineData("ssh-dss " + EncodedPublicKey)]
    [InlineData("SSH-ED25519 " + EncodedPublicKey)]
    [InlineData(" " + PublicKey)]
    [InlineData(PublicKey + " ")]
    [InlineData("ssh-ed25519  " + EncodedPublicKey)]
    [InlineData(PublicKey + " comment")]
    [InlineData(PublicKey + "\r\n")]
    [InlineData("ssh-ed25519\t" + EncodedPublicKey)]
    public void RejectsMalformedMismatchedOrNoncanonicalPins(string? value) =>
        Assert.False(SftpHostKeyValidator.IsValid(value));

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, true)]
    [InlineData(16_384, true)]
    [InlineData(16_385, false)]
    public void EnforcesTheExistingDecodedPayloadSizeBounds(int size, bool expected)
    {
        var blob = new byte[size];
        const string algorithm = "ssh-ed25519";
        BinaryPrimitives.WriteInt32BigEndian(blob, algorithm.Length);
        Encoding.ASCII.GetBytes(algorithm).CopyTo(blob, 4);
        Assert.Equal(expected, SftpHostKeyValidator.IsValid(algorithm + " " + Convert.ToBase64String(blob)));
    }

    [Fact]
    public void RejectsOversizedEncodedInputAndInvalidAlgorithmLengthWithoutThrowing()
    {
        Assert.False(SftpHostKeyValidator.IsValid("ssh-ed25519 " + new string('A', 24_001)));
        var blob = Convert.FromBase64String(EncodedPublicKey);
        BinaryPrimitives.WriteInt32BigEndian(blob, int.MaxValue);
        Assert.False(SftpHostKeyValidator.IsValid("ssh-ed25519 " + Convert.ToBase64String(blob)));
    }
}
