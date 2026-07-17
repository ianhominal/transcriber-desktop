using System.Text.Json;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AuthUserTests
{
    [Fact]
    public void DisplayName_PrefiereName_SobreFullName()
    {
        var user = new AuthUser
        {
            Email = "a@b.com",
            UserMetadata = new UserMetadata { Name = "Ian", FullName = "Ian Hominal" },
        };

        Assert.Equal("Ian", user.DisplayName);
    }

    [Fact]
    public void DisplayName_SinName_CaeAFullName()
    {
        var user = new AuthUser
        {
            Email = "a@b.com",
            UserMetadata = new UserMetadata { FullName = "Ian Hominal" },
        };

        Assert.Equal("Ian Hominal", user.DisplayName);
    }

    [Fact]
    public void DisplayName_SinMetadata_CaeAEmail()
    {
        var user = new AuthUser { Email = "a@b.com" };

        Assert.Equal("a@b.com", user.DisplayName);
    }

    [Fact]
    public void DisplayName_MetadataConCamposVacios_CaeAEmail()
    {
        var user = new AuthUser
        {
            Email = "a@b.com",
            UserMetadata = new UserMetadata { Name = "  ", FullName = "" },
        };

        Assert.Equal("a@b.com", user.DisplayName);
    }

    [Fact]
    public void AvatarUrl_PrefiereAvatarUrl_SobrePicture()
    {
        var user = new AuthUser
        {
            UserMetadata = new UserMetadata { AvatarUrl = "https://a/avatar.png", Picture = "https://a/picture.png" },
        };

        Assert.Equal("https://a/avatar.png", user.AvatarUrl);
    }

    [Fact]
    public void AvatarUrl_SinAvatarUrl_CaeAPicture()
    {
        var user = new AuthUser
        {
            UserMetadata = new UserMetadata { Picture = "https://a/picture.png" },
        };

        Assert.Equal("https://a/picture.png", user.AvatarUrl);
    }

    [Fact]
    public void AvatarUrl_SinMetadata_QuedaVacio()
    {
        var user = new AuthUser { Email = "a@b.com" };

        Assert.Equal(string.Empty, user.AvatarUrl);
    }

    [Fact]
    public void Initials_UsaDisplayName()
    {
        var user = new AuthUser
        {
            Email = "a@b.com",
            UserMetadata = new UserMetadata { Name = "Ian Hominal" },
        };

        Assert.Equal("IH", user.Initials);
    }

    [Fact]
    public void Deserializa_UserMetadataDeGoogle_ConFullNameYAvatarUrl()
    {
        var json = """
            {"id":"u1","email":"ian@gmail.com","user_metadata":{"full_name":"Ian Hominal","avatar_url":"https://lh3.googleusercontent.com/a/foto.jpg"}}
            """;

        var user = JsonSerializer.Deserialize<AuthUser>(json)!;

        Assert.Equal("Ian Hominal", user.DisplayName);
        Assert.Equal("https://lh3.googleusercontent.com/a/foto.jpg", user.AvatarUrl);
    }

    [Fact]
    public void Deserializa_UserMetadataConPictureEnVezDeAvatarUrl()
    {
        var json = """
            {"id":"u1","email":"ian@gmail.com","user_metadata":{"name":"Ian","picture":"https://lh3.googleusercontent.com/a/foto.jpg"}}
            """;

        var user = JsonSerializer.Deserialize<AuthUser>(json)!;

        Assert.Equal("Ian", user.DisplayName);
        Assert.Equal("https://lh3.googleusercontent.com/a/foto.jpg", user.AvatarUrl);
    }

    [Fact]
    public void Deserializa_SinUserMetadata_NoRompe()
    {
        var json = """{"id":"u1","email":"a@b.com"}""";

        var user = JsonSerializer.Deserialize<AuthUser>(json)!;

        Assert.Equal("a@b.com", user.DisplayName);
        Assert.Equal(string.Empty, user.AvatarUrl);
    }
}
