# Esquema Real y Auditoría de Fuentes Externas (IPTV Multi-Nivel)

Fecha de auditoría: 12 de noviembre de 2026.
Auditoría técnica previa a la implementación de FASE 2, FASE 3 y FASE B/C en Lex TV.

---

## 1. Fuentes Evaluadas

| Fuente | Nivel de Confianza | URL | Estado HTTP | Tamaño | Entradas |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **legalFree live.m3u** | Nivel 1 (Oficial verificado) | `https://biskyt.github.io/legalFreeIPTVplaylist/live.m3u` | 200 OK | 0.9 KB | 3 |
| **Free-TV playlist.m3u8** | Nivel 2 (Curado gratuito) | `https://raw.githubusercontent.com/Free-TV/IPTV/master/playlist.m3u8` | 200 OK | 570.3 KB | 2,203 |
| **iptv-org** | Nivel 3 (Comunitario) | `https://iptv-org.github.io/api/*` (catálogo local) | Local / 200 | 2.5 MB | 5,374 |
| **Archive Watch VOD** | Nivel VOD (Evaluado) | `https://archivewatch.org/feeds/archivewatch.m3u` | 200 OK | 18.9 MB | 49,405 |

---

## 2. Atributos Presentes por Fuente

| Atributo | legalFree live.m3u (3) | Free-TV playlist.m3u8 (2,203) | Archive Watch VOD (49,405) |
| :--- | :--- | :--- | :--- |
| `tvg-id` | 3 (100.0%) | 1,634 (74.2%) | 49,405 (100.0%) |
| `group-title` | 3 (100.0%) | 2,203 (100.0%) | 49,405 (100.0%) |
| `tvg-country` / `country` | 3 (100.0%) | 1,932 (87.7%) | 0 (0.0%) |
| `tvg-language` / `language` | 3 (100.0%) | 0 (0.0%) | 0 (0.0%) |
| `tvg-logo` / `logo` | 3 (100.0%) | 2,111 (95.8%) | 49,264 (99.7%) |

### Indicadores Especiales en Nombres de Free-TV:
- `Ⓖ`: Geobloqueado (restringido a un territorio específico).
- `Ⓢ`: Definición estándar (SD / baja resolución).
- `Ⓨ`: Enlace de emisión en vivo de YouTube (no decodificable por Chromium/HLS crudo sin reproductor de YouTube; se debe delegar a apertura externa/pestaña normal).

---

## 3. Formatos de URL de Medios

| Formato | legalFree live | Free-TV playlist | Archive Watch VOD |
| :--- | :--- | :--- | :--- |
| **HLS (`.m3u8`)** | 3 (100.0%) | 1,937 (87.9%) | 0 (0.0%) |
| **MP4 directo** | 0 (0.0%) | 1 (0.05%) | 49,297 (99.8%) |
| **YouTube Livestream** | 0 (0.0%) | 129 (5.9%) | 4 (0.01%) |
| **MPEG-TS (`.ts`)** | 0 (0.0%) | 0 (0.0%) | 0 (0.0%) |
| **Otros / Streams dinámicos**| 0 (0.0%) | 136 (6.2%) | 104 (0.2%) |

---

## 4. Solape con iptv-org (Catálogo Local de 5,374 canales)

| Fuente | Solape por `tvg-id` | Solape por `URL` Normalizada | Conclusión de Fusión |
| :--- | :--- | :--- | :--- |
| **legalFree live.m3u** | 1 (33.3%) | 1 (33.3%) | Clave primaria `tvg-id` fusiona perfectamente; 2 canales nuevos de alta confianza. |
| **Free-TV playlist.m3u8** | 0 (0.0%)* | 207 (9.4%) | *Free-TV usa esquemas de id no estándar en muchos canales (e.g. `Nombre.pe` vs `pe.nombre`). La clave secundaria por URL normalizada detecta 207 duplicados exactos y los fusiona como alternativos con Nivel 2. |
| **Archive Watch VOD** | 0 (0.0%) | 0 (0.0%) | Lista de 49,405 MP4 sin licencias verificadas por ítem. |

---

## 5. Dictamen sobre Archive Watch VOD (Regla Estricta)

**Decisión:** **OMITIR** la incorporación automática de Archive Watch VOD en las fuentes activas.
**Motivos técnicos:**
1. **Presupuesto de memoria y red:** El archivo M3U pesa **18.9 MB** sin comprimir. Su parseo e indexación exigiría entre 60 y 90 MB de heap en memoria, violando la regla de bajo impacto y cero overhead de Lex TV.
2. **Falta de metadatos de licencias:** A diferencia de `legalFree` (`catalog.json`), Archive Watch no provee campos de licencia (`license`), páginas de atribución por título ni desgloses de dominio público en su M3U.
3. **Cine ya está cubierto con legalFree:** La sección Cine actual ya provee 906 películas verificadas bajo dominio público y licencias Creative Commons en `upload.wikimedia.org` y `archive.org`.

