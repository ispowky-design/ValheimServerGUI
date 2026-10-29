## Télécharger

**Windows (recommandé) :** `ValheimServerGUI-<version>-win-x64-setup.exe`. Lance-le, il installe l'application
pour ton compte (sans droits administrateur), avec une entrée dans le menu Démarrer et un raccourci sur le
bureau si tu le coches.

**Windows, sans installation :** `ValheimServerGUI-<version>-win-x64-portable.exe`, un seul fichier à lancer
où tu veux.

**Linux :** `ValheimServerGUI-<version>-linux-x64.tar.gz`, à extraire puis lancer `ValheimServerGUI.App`.

Aucun prérequis : le runtime .NET est inclus. Tes profils, réglages et sauvegardes restent dans ton dossier
utilisateur, une mise à jour ou une désinstallation n'y touche pas.

Windows SmartScreen peut afficher « Windows a protégé votre ordinateur » parce que l'exécutable n'est pas
signé : clique sur « Informations complémentaires » puis « Exécuter quand même ».

**Mises à jour :** à partir de la 3.4.0, l'application télécharge elle-même la nouvelle version en arrière-plan.
Clique sur « Mise à jour … prête à installer » dans la barre d'état : elle s'installe et l'application redémarre.
Elle refuse de s'installer tant qu'un serveur tourne. Depuis une version plus ancienne, lance une fois le nouvel
installateur par-dessus (ou remplace l'exe portable).

**Numérotation :** l'application est en alpha et ses versions s'appellent désormais 0.0.x-alpha. Elles
remplacent l'ancienne numérotation 3.x : la mise à jour automatique les considère comme plus récentes que
n'importe quelle 3.x. Une copie restée sur une 3.x antérieure à la 3.9.0 ne les voit pas : lance une fois le
nouvel installateur par-dessus.

`SHA256SUMS.txt` donne l'empreinte de chaque fichier ; la mise à jour automatique la vérifie avant d'installer.
