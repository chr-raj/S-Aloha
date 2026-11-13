# 2026-11-11 — SOC-23 : les notifications d'une déclaration suivent l'incident déplacé

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : quand un incident change de périmètre, ses déclarations réglementaires le suivent
  (DRG-01) et, désormais, les notifications de ces déclarations aussi (« Déclaration
  réglementaire créée », relances DRG-13), dans le même enregistrement. Les gestionnaires du
  nouveau périmètre les lisent dans la cloche et les courriels ; ceux de l'ancien, plus.
- **Pourquoi** : SOC-23 veut que les notifications d'un enregistrement le suivent quand il
  change de périmètre ; `RecordController.Update` ne déplaçait que celles de l'incident.
- **Vérifié** : le test `SOC_23_DRG_13_les_notifications_d_une_declaration_suivent_l_incident_deplace`
  échoue sans la correction (l'ancien gestionnaire lit toujours la notification) et passe avec ;
  tests `SOC_23`, `PER_07` et `DRG` verts.
- **Fichiers** : `backend/Modules/IncidentManagement/Controllers/IncidentsController.cs`,
  `tests/SAloha.Api.Tests/NotificationsTests.cs`
- **Documentation** : spécification des déclarations réglementaires (DRG-01)
- **Commit** : SOC-23, notifications des déclarations
