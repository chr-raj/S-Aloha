### Sécurité

- **Contrôles des pull requests** : une PR prouve son changement par un test qui échoue sans
  lui (job Preuve), ne peut ni retirer ni désactiver de test (Garde-fous), et ses dépendances
  ajoutées comme ses workflows sont analysés ; une CI rouge sur `main` ouvre une issue
  `main-rouge`. Deux injections par expression corrigées dans les workflows de publication
  d'images.
