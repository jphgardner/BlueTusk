using System.Text;

namespace BlueTusk.Security.Tests;

public sealed class BlueTuskPostgreSqlMd5PasswordTests
{
    [Fact]
    public void Matches_the_PostgreSQL_MD5_challenge_formula()
    {
        var response = BlueTuskPostgreSqlMd5Password.CreateResponse(
            "user",
            "pencil",
            new byte[] { 0x12, 0x34, 0x56, 0x78 });

        try
        {
            Assert.Equal("md580cd925042851e77d703d2e1aba480ba", Encoding.ASCII.GetString(response));
        }
        finally
        {
            BlueTuskSensitiveBuffer.Clear(response);
        }
    }

    [Fact]
    public void Requires_the_four_byte_PostgreSQL_salt()
    {
        Assert.Throws<ArgumentException>(
            () => BlueTuskPostgreSqlMd5Password.CreateResponse("user", "password", new byte[3]));
    }
}
