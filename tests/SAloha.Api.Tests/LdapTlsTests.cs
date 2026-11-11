using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SAloha.Api.Core.Auth;

namespace SAloha.Api.Tests;

/// <summary>S2 : LDAPS accepte la chaîne de l'autorité configurée, et rien d'autre.</summary>
public class LdapTlsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ldaptls").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static (X509Certificate2 Ca, X509Certificate2 Leaf) Chain(string caName, string host)
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest($"CN={caName}", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (ca, Leaf(ca, host, ServerAuth));
    }

    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    /// <summary>Certificat du serveur <paramref name="host"/> émis par <paramref name="ca"/> pour l'usage donné.</summary>
    private static X509Certificate2 Leaf(X509Certificate2 ca, string host, string usage)
    {
        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest($"CN={host}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        leafReq.CertificateExtensions.Add(san.Build());
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        return leafReq.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(8));
    }

    private string WritePem(string name, params X509Certificate2[] certs)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Concat(certs.Select(c => c.ExportCertificatePem() + "\n")));
        return path;
    }

    [Fact]
    public void Sans_autorite_configuree_la_validation_par_defaut_reste_en_place()
    {
        Assert.Null(LdapTls.Validator(null));
        Assert.Null(LdapTls.Validator("  "));
    }

    [Fact]
    public void Un_certificat_signe_par_l_autorite_configuree_est_accepte()
    {
        var (ca, leaf) = Chain("CA de l'annuaire", "dc01.mondomaine.local");
        var validator = LdapTls.Validator(WritePem("ca.pem", ca))!;
        Assert.True(validator(this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Un_certificat_d_une_autre_autorite_est_refuse()
    {
        var (ca, _) = Chain("CA de l'annuaire", "dc01.mondomaine.local");
        var (_, intrus) = Chain("Autre CA", "dc01.mondomaine.local");
        var validator = LdapTls.Validator(WritePem("ca.pem", ca))!;
        Assert.False(validator(this, intrus, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Un_certificat_de_l_autorite_sans_usage_serveur_est_refuse()
    {
        // La validation du système exige l'usage « authentification serveur » ; l'autorité
        // configurée ne rattrape pas un certificat qu'elle a émis pour un autre usage.
        var (ca, serveur) = Chain("CA de l'annuaire", "dc01.mondomaine.local");
        using var client = Leaf(ca, "dc01.mondomaine.local", ClientAuth);
        var validator = LdapTls.Validator(WritePem("ca.pem", ca))!;
        Assert.True(validator(this, serveur, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validator(this, client, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Un_nom_different_reste_refuse_meme_signe_par_l_autorite()
    {
        var (ca, leaf) = Chain("CA de l'annuaire", "autre-serveur.mondomaine.local");
        var validator = LdapTls.Validator(WritePem("ca.pem", ca))!;
        Assert.False(validator(this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(validator(this, leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Certificat_absent_refuse_et_validation_reussie_acceptee()
    {
        var (ca, leaf) = Chain("CA de l'annuaire", "dc01.mondomaine.local");
        var validator = LdapTls.Validator(WritePem("ca.pem", ca))!;
        Assert.False(validator(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.True(validator(this, leaf, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Un_fichier_avec_plusieurs_autorites_les_accepte_toutes()
    {
        var (ca1, leaf1) = Chain("CA 1", "dc01.mondomaine.local");
        var (ca2, leaf2) = Chain("CA 2", "dc02.mondomaine.local");
        var validator = LdapTls.Validator(WritePem("ca.pem", ca1, ca2))!;
        Assert.True(validator(this, leaf1, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(validator(this, leaf2, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Un_fichier_introuvable_ou_invalide_donne_une_erreur_claire()
    {
        var absent = Assert.Throws<InvalidOperationException>(() => LdapTls.Validator(Path.Combine(_dir, "absent.pem")));
        Assert.Contains("introuvable", absent.Message);
        var vide = Path.Combine(_dir, "vide.pem");
        File.WriteAllText(vide, "pas un certificat");
        var invalide = Assert.Throws<InvalidOperationException>(() => LdapTls.Validator(vide));
        Assert.Contains("aucun certificat", invalide.Message);
    }
}
