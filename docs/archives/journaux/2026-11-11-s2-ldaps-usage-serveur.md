# 2026-11-11 — S2 : LDAPS exige l'usage « authentification serveur » avec l'autorité de l'annuaire

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : avec `Ldap:CaCertificatePath`, `LdapTls.Accepts` reconstruit la chaîne du
  contrôleur de domaine en exigeant l'usage étendu « authentification serveur »
  (1.3.6.1.5.5.7.3.1) ; un certificat de l'autorité émis pour un autre usage est refusé.
- **Pourquoi** : la validation du système, que l'autorité configurée remplace pour la seule
  chaîne, exige cet usage ; le rattrapage doit garder la même exigence (complément de S2).
- **Vérifié** : le test `Un_certificat_de_l_autorite_sans_usage_serveur_est_refuse` échoue
  sans la correction et passe avec ; les autres `LdapTlsTests` restent verts.
- **Fichiers** : `backend/Core/Auth/LdapTls.cs`, `tests/SAloha.Api.Tests/LdapTlsTests.cs`
- **Documentation** : exploitation (§ LDAPS)
- **Commit** : S2, usage du certificat LDAPS
