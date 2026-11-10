using System.Net.Security;
using Novell.Directory.Ldap;
using System.Security.Cryptography.X509Certificates;

namespace SAloha.Api.Core.Auth;

/// <summary>
/// Confiance en l'autorité de certification d'un annuaire interne pour LDAPS (S2).
/// Une AD d'entreprise présente un certificat émis par sa propre autorité, absente du magasin
/// du conteneur : sans ce réglage, LDAPS échoue. <c>Ldap:CaCertificatePath</c> désigne un fichier
/// PEM (une ou plusieurs autorités) ; la chaîne du serveur doit alors y aboutir.
/// La validation n'est <b>jamais</b> désactivée : une erreur autre qu'une chaîne inconnue
/// (nom du serveur différent de <c>Ldap:Host</c>, certificat absent) reste un refus.
/// </summary>
public static class LdapTls
{
    /// <summary>
    /// Validateur à brancher sur la connexion, ou <c>null</c> (validation par défaut du système)
    /// quand aucune autorité n'est configurée.
    /// </summary>
    public static System.Net.Security.RemoteCertificateValidationCallback? Validator(string? caCertificatePath)
    {
        if (string.IsNullOrWhiteSpace(caCertificatePath)) return null;
        var authorities = Load(caCertificatePath);
        return (_, certificate, _, errors) => Accepts(certificate, errors, authorities);
    }

    /// <summary>
    /// Branche l'autorité configurée sur une connexion LDAPS (sans effet hors LDAPS ou sans
    /// <c>Ldap:CaCertificatePath</c>). Lève <see cref="InvalidOperationException"/> si le fichier est
    /// introuvable ou invalide.
    /// </summary>
    public static void Apply(LdapConnection conn, IConfiguration config)
    {
        if (!conn.SecureSocketLayer) return;
        if (Validator(config["Ldap:CaCertificatePath"]) is { } validator)
            conn.UserDefinedServerCertValidationDelegate += new Novell.Directory.Ldap.RemoteCertificateValidationCallback(validator.Invoke);
    }

    /// <summary>Charge le fichier PEM ; une erreur claire plutôt qu'un échec LDAPS obscur.</summary>
    public static X509Certificate2Collection Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Ldap:CaCertificatePath : fichier introuvable ({path}).");
        var authorities = new X509Certificate2Collection();
        try { authorities.ImportFromPemFile(path); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Ldap:CaCertificatePath : {path} n'est pas un fichier PEM de certificats.", ex);
        }
        if (authorities.Count == 0)
            throw new InvalidOperationException($"Ldap:CaCertificatePath : {path} ne contient aucun certificat.");
        return authorities;
    }

    public static bool Accepts(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2Collection authorities)
    {
        if (errors == SslPolicyErrors.None) return true;
        // Seule une chaîne non reconnue est rattrapée par l'autorité configurée ; le reste
        // (nom différent, certificat absent) reste un refus.
        if (certificate is null || errors != SslPolicyErrors.RemoteCertificateChainErrors) return false;

        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
        // Pas de contrôle de révocation en ligne : un annuaire interne n'a souvent ni CRL ni OCSP joignable.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }
}
