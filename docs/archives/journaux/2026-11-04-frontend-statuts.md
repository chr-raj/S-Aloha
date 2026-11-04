# 2026-11-04 — Référence de l'interface : six écrans marqués 🔜 déjà livrés

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : dans `docs/reference/frontend.md`, six entrées passent de 🔜 à ✅, avec
  l'emplacement de l'écran : respect des SLA (SLM-20, SLM-21), catalogue de demandes
  (REQ-20), modèles de changement standard (CHG-26), périodes de gel (CHG-25), import des CI
  (CFG-30), heures de service (SLM-14).
- **Pourquoi** : les règles sont ✅ dans la référence des pratiques et les écrans existent —
  `frontend/src/core/pages/reporting/ComplianceTab.jsx:69`,
  `frontend/src/modules/registry.js:45` et `:63`,
  `frontend/src/modules/change-enablement/pages/ChangeSchedule.jsx:21`,
  `frontend/src/modules/service-configuration-management/pages/ConfigurationItems.jsx:24`,
  `frontend/src/modules/service-level-management/config.jsx:17`.
- **Non modifié** : les filtres prédéfinis du registre des incidents et les boutons de
  transition nommés restent « ✅ / 🔜 », faute de preuve qu'ils sont complets.
- **Fichiers** : `docs/reference/frontend.md`
- **Commit** : six écrans de l'interface marqués livrés
