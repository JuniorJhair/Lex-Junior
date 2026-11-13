# Documento de Inspección: Esquema Real de Cine VOD (Fase 0)

Fecha de inspección: 12 de noviembre de 2026.  
Fuente principal analizada: `https://biskyt.github.io/legalFreeIPTVplaylist/`

---

## 1. Análisis de Archivos Remotos

| Archivo | Tamaño | Registros / Líneas | Formato / Tipo | Propósito |
|---|---|---|---|---|
| `catalog.json` | **1.33 MB** (1,394,124 bytes) | 909 items | JSON (`{ generated, format, feeds, items: [] }`) | Fuente principal de metadatos, licencias y sinopsis |
| `vod.m3u` | **0.60 MB** (627,700 bytes) | 2,456 líneas / 906 títulos | M3U extendido con atributos VOD | Respaldo si `catalog.json` no estuviera disponible |
| `movies.m3u` | **0.60 MB** (627,700 bytes) | 2,456 líneas / 906 títulos | M3U extendido | Idéntico a `vod.m3u` |
| `series.m3u` | **8 bytes** | 2 líneas (`#EXTM3U\n`) | M3U | Actualmente vacío en la fuente remota |
| `report.json` | **35.3 KB** (36,150 bytes) | N/A | JSON | Estadísticas del proceso de curación y decisiones |

---

## 2. Esquema Real de `catalog.json`

Estructura raíz:
```json
{
  "generated": "2026-11-12T12:40:12+00:00",
  "format": "https://github.com/biskyt/legalFreeIPTVplaylist/blob/main/FORMAT.md",
  "feeds": { "live": 3, "movies": 906, "series": 0, "vod": 906, "playlist": 909 },
  "items": [ /* array de 909 objetos */ ]
}
```

### Campos reales observados en cada `item` de `items`:

| Campo | Tipo | Ejemplo Real | Descripción |
|---|---|---|---|
| `id` | `string` | `"ia.ElHotelElectrico"` | Identificador único y estable |
| `kind` | `string` | `"movie"` o `"live"` | Tipo de contenido (`movie` para VOD, `live` para TV) |
| `title` | `string` | `"El Hotel Eléctrico (1908)"` | Título del contenido |
| `group` | `string` | `"Movies: Shorts"`, `"Movies: World Classics"` | Género / Colección |
| `url` | `string` | `"https://archive.org/download/..."` | URL del medio (MP4, WebM, OGV o M3U8) |
| `year` | `number` (opcional) | `1908` | Año de publicación |
| `duration` | `number` (opcional) | `572` | Duración total en segundos (~10 min) |
| `logo` | `string` (opcional) | `"https://archive.org/services/img/ElHotelElectrico"` | Carátula / Poster / Miniatura |
| `languages` | `string[]` | `["spa"]`, `["eng"]`, `["zxx"]` | Códigos ISO 639-3 de audio/idioma |
| `countries` | `string[]` (opcional) | `["ES"]`, `["US"]` | Países de origen |
| `tmdb` | `string` (opcional) | `"143634"` | ID numérico en TheMovieDB |
| `imdb` | `string` (opcional) | `"tt0000516"` | ID de IMDb (`tt...`) |
| `license` | `string` | `"Public domain in the US (as marked on the Internet Archive)"` | Licencia declarada |
| `page` | `string` (opcional) | `"https://archive.org/details/ElHotelElectrico"` | Página web original en la fuente |
| `description` | `string` (opcional) | `"The Electric Hotel starring Segundo de Chomón..."` | Sinopsis / Descripción |
| `source` | `string` | `"internet-archive"`, `"wikimedia-commons"`, `"blender"` | Nombre de la fuente de procedencia |
| `attribution`| `string` (opcional) | `"Government of the District of Columbia..."` | Atribución explícita de autor |
| `legal` | `object` (opcional) | `{ basis, title, text, links, terms }` | Justificación jurídica del dominio público |

### Campos definidos en convención `FORMAT.md` para Series:
- `tvg-type`: `"series"`
- `series-name`: `string` (Nombre de la serie, p. ej. `"The Beverly Hillbillies"`)
- `season`: `number` (Número de temporada)
- `episode`: `number` (Número de episodio)

---

## 3. Desglose de Contenido y Estadísticas

- **Total de registros**: 909
  - **Películas VOD (`movie`)**: 906
  - **Transmisiones en vivo (`live`)**: 3 (descartadas por la allowlist de VOD)
  - **Series (`series`)**: 0 en el catálogo activo actual (pero preparadas en el modelo según `FORMAT.md`)
- **Fuentes principales**:
  - `wikimedia-commons`: 642 títulos (70.6%)
  - `internet-archive`: 253 títulos (27.8%)
  - `blender`: 11 títulos (1.2%)
- **Hosts de video**:
  - `upload.wikimedia.org`: 642 títulos
  - `archive.org`: 264 títulos
  - `akamaized.net` / `video.oct.dc.gov`: 3 títulos (en vivo, descartados)
- **Licencias predominantes**:
  - Dominio público EE. UU. / Pre-1931: 852 títulos
  - Creative Commons (CC BY 3.0 / CC BY 4.0 / CC0): 47 títulos
- **Idiomas**:
  - `zxx` (Mudas / Sin idioma hablado): 337 títulos
  - `eng` (Inglés): 111 títulos declarados en el array (y ~448 en lista general)
  - `fra` (Francés): 4 títulos
  - `spa` (Español): 1 título (`El Hotel Eléctrico (1908)`)
  - Otros: `fas` (1), `zho` (1), `deu` (1), `mis` (1)

---

## 4. Estado de Listas por Idioma (`languages/spa.m3u`)
- `https://biskyt.github.io/legalFreeIPTVplaylist/languages/spa.m3u` devuelve **HTTP 404 (Not Found)**.
- `https://biskyt.github.io/legalFreeIPTVplaylist/languages/es.m3u` devuelve **HTTP 404 (Not Found)**.
- Solo existe actualmente `languages/eng.m3u` (448 entradas).
- **Conclusión**: El catálogo actual contiene únicamente **1 título en español** (`ia.ElHotelElectrico`). La UI no debe fallar si no existe `spa.m3u`; el filtrado por idioma debe operar de manera unificada sobre la propiedad `languages` de `catalog.json`.

---

## 5. Pruebas de Red y Compatibilidad (Range 206 y Codecs)

### Archive.org (5 URLs probadas)
1. `https://archive.org/download/TripDown1905/TripDown1905.mp4`
2. `https://archive.org/download/FindingH1929/FindingH1929.mp4`
3. `https://archive.org/download/GeneralH1928/GeneralH1928.mp4`
4. `https://archive.org/download/HowtoUse1927/HowtoUse1927.mp4`
5. `https://archive.org/download/6090_Sky_A_Film_Lesson_in_Nature_Study_The_01_35_14_18/6090_Sky_A_Film_Lesson_in_Nature_Study_The_01_35_14_18.mp4`

- **Comportamiento**:
  - Devuelven `HTTP 302 Found` redirigiendo a nodos de almacenamiento como `https://dn790001.ca.archive.org/0/items/...` o `https://ia800105.us.archive.org/...` (ambos cumplen la allowlist `*.archive.org`).
  - Al solicitar con cabecera `Range: bytes=0-1023`, el destino devuelve **`HTTP 206 Partial Content`**.
  - `Content-Type`: `video/mp4` (H.264 Baseline/Main con AAC).
  - `Content-Range`: `bytes 0-1023/<tamaño_total>`.
  - **Soporte de seeking**: Total y nativo en Chromium sin intermediación del proxy.

### Wikimedia Commons (3 URLs probadas)
1. `https://upload.wikimedia.org/wikipedia/commons/transcoded/1/19/Apollo_13_-_Home_Safe.webm/Apollo_13_-_Home_Safe.webm.480p.vp9.webm`
2. `https://upload.wikimedia.org/wikipedia/commons/transcoded/2/23/Apollo_13_Houston%2C_We%27ve_Got_a_Problem.ogv/Apollo_13_Houston%2C_We%27ve_Got_a_Problem.ogv.240p.vp9.webm`
3. `https://upload.wikimedia.org/wikipedia/commons/transcoded/f/f7/Apollo_14_-_Mission_to_Fra_Mauro.webm/Apollo_14_-_Mission_to_Fra_Mauro.webm.480p.vp9.webm`

- **Comportamiento**:
  - Responden directamente desde `upload.wikimedia.org` (sin redirecciones).
  - Con cabecera `Range: bytes=0-1023`, responden con **`HTTP 206 Partial Content`**.
  - `Content-Type`: `video/webm` (VP9 con Vorbis o Opus).
  - `Content-Range`: `bytes 0-1023/<tamaño_total>`.
  - `Accept-Ranges`: `bytes`.
  - **Soporte de seeking**: Total y nativo en Chromium.

---

## 6. Estrategia de Memoria (Fase 1)

1. **Tamaño del Catálogo**: El archivo `catalog.json` completo pesa **1.33 MB**.
2. **Descarga Condicional**:
   - `TvCineService` almacena el catálogo en caché de disco local (`cine_catalog_cache.json`) con TTL de 24 horas.
   - En peticiones posteriores envía cabeceras condicionales `If-None-Match` (ETag) e `If-Modified-Since`. Si el servidor responde `304 Not Modified`, no transfiere datos.
3. **Parseo en Streaming**:
   - Se utiliza `JsonDocument.ParseAsync(stream)` directamente desde el stream HTTP o de archivo, sin materializar el contenido como string C#.
4. **Índice Compacto en Memoria**:
   - Para los ~906 títulos, se mantiene en memoria únicamente un registro ligero:
     - `Id` (string)
     - `Title` (string)
     - `TitleNormalized` (string sin acentos ni mayúsculas para búsqueda instantánea)
     - `Year` (short)
     - `Duration` (int)
     - `Group` (string / categoría)
     - `Logo` (string / URL de poster)
     - `Languages` (string[])
     - `Kind` (byte: Movie / Series)
     - `Source` (string)
     - `License` (string)
     - `MediaUrl` (string)
   - Esto ocupa menos de **250 KB de RAM** en total.
5. **Cero Recursos con Cine Cerrado**:
   - Al cerrar la pestaña de TV o llamar a `TvStudioService.ReleaseAll()`, el índice de Cine se libera (`_memoryIndex = null`), los CancellationTokenSources se disponen y no queda ningún temporizador, socket o hilo en segundo plano.

