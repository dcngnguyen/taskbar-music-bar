using System.Runtime.InteropServices;
using Windows.Security.Credentials;

namespace MusicBar;

/// <summary>Keeps the Spotify login token in Windows Credential Manager (encrypted to your Windows account).</summary>
internal static class TokenStore
{
    private const string Resource = "MusicBar.Spotify";
    private const string UserName = "refresh_token";
    private const int ElementNotFound = unchecked((int)0x80070490);

    public static string? Load()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, UserName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (COMException ex) when (ex.HResult == ElementNotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Could not read Spotify token: " + ex.Message);
            return null;
        }
    }

    public static void Save(string refreshToken)
    {
        var vault = new PasswordVault();
        Remove(vault);
        vault.Add(new PasswordCredential(Resource, UserName, refreshToken));
    }

    public static void Clear() => Remove(new PasswordVault());

    private static void Remove(PasswordVault vault)
    {
        try { vault.Remove(vault.Retrieve(Resource, UserName)); }
        catch (COMException ex) when (ex.HResult == ElementNotFound) { }
    }
}
