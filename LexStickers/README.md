# LexSticker — Creador Web de Stickers con Audio para WhatsApp

Crea stickers de WhatsApp (`512×512 px`) con o sin audio desde archivos locales (video, GIF o foto), añade música MP3 o graba tu propia voz y envíalo **directo a tu WhatsApp** (**100 % gratis**).

- **Multi-usuario aislado:** cada visitante recibe un identificador de sesión independiente (`x-lex-session`) para generar su propio QR / código de 8 dígitos de WhatsApp sin compartir chats ni archivos con otros usuarios.
- **Optimizado para Render Free (512 MB RAM / Docker):** serializa conversiones FFmpeg (`-threads 1`) y purga archivos temporales automáticamente cada 5 minutos.
