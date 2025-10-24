# FullDurabilityRepair

Plugin de Rust para uMod que permite reparar o rellenar objetos inmediatamente a su durabilidad máxima para los jugadores con permiso. El uso puede limitarse mediante enfriamientos, límites diarios, distinción VIP y comandos administrativos.

## Características
- Reparación instantánea al 100 % para objetos con condición.
- Límites diarios y enfriamientos configurables (valores independientes para VIP).
- Reinicio automático de contadores en intervalos configurables.
- Permisos especiales para administración y para omitir todas las restricciones.
- Guardado periódico configurable y guardado diferido automático para reducir escrituras.
- Persistencia compatible con anonimización de IDs y migración automática de datos antiguos.
- Mensajes localizados en inglés, español y portugués brasileño.

## Permisos
| Permiso | Descripción |
| --- | --- |
| `fulldurabilityrepair.use` | Permite usar reparaciones con los límites y enfriamientos configurados. |
| `fulldurabilityrepair.vip` | Usa los valores VIP de límite diario y enfriamiento. |
| `fulldurabilityrepair.norestriction` | Permite reparar sin restricciones ni contadores. |
| `fulldurabilityrepair.admin` | Permite ejecutar comandos administrativos y recargar la configuración. |

## Comandos
Los comandos se ejecutan desde el chat del juego.

| Comando | Permiso | Descripción |
| --- | --- | --- |
| `/fdrinfo` | `fulldurabilityrepair.use` o superior | Muestra el número de reparaciones restantes para el día. |
| `/fdradminreset` | `fulldurabilityrepair.admin` | Reinicia todos los contadores diarios. |
| `/fdrvipreset` | `fulldurabilityrepair.admin` | Reinicia los contadores solo de jugadores VIP. |
| `/fdradmininfo` | `fulldurabilityrepair.admin` | Lista los comandos administrativos disponibles y la configuración relevante. |
| `/fdrsetlimit <n>` | `fulldurabilityrepair.admin` | Cambia el límite diario general. |
| `/fdrcooldown <segundos>` | `fulldurabilityrepair.admin` | Cambia el enfriamiento general. |
| `/fdrviplimit <n>` | `fulldurabilityrepair.admin` | Cambia el límite diario para VIP. |
| `/fdrvipcooldown <segundos>` | `fulldurabilityrepair.admin` | Cambia el enfriamiento para VIP. |
| `/fdrreload` | `fulldurabilityrepair.admin` | Recarga la configuración desde el archivo de configuración. |

## Configuración
La configuración se guarda en `oxide/config/FullDurabilityRepair.json`.

| Clave | Tipo | Descripción |
| --- | --- | --- |
| `CooldownSeconds` | entero | Enfriamiento en segundos para jugadores normales. |
| `DailyLimit` | entero | Límite diario de reparaciones para jugadores normales (0 = ilimitado). |
| `VipCooldownSeconds` | entero | Enfriamiento en segundos para jugadores con permiso VIP. |
| `VipDailyLimit` | entero | Límite diario para jugadores VIP (0 = ilimitado). |
| `EnableDailyReset` | bool | Habilita el reinicio automático de contadores. |
| `ResetIntervalSeconds` | entero | Intervalo del reinicio automático en segundos. |
| `AnonymizeIds` | bool | Guarda los datos con IDs hash en disco manteniendo un mapa reversible para restaurarlos. |
| `SaveIntervalSeconds` | entero | Intervalo en segundos para guardados periódicos (0 = deshabilitado). |
| `Message*` | string | Personaliza los mensajes mostrados al jugador. |

## Persistencia y anonimización
- El plugin guarda los contadores en `oxide/data/FullDurabilityRepair_Data.json`.
- Cuando `AnonymizeIds` está activo, los datos se escriben con IDs hash acompañados de un mapa hash→ID que permite restaurarlos al cargar, evitando pérdida de historial.
- Las entradas creadas con versiones antiguas del plugin que solo almacenaban hashes se migran automáticamente en cuanto el jugador vuelve a usar el sistema. Mientras tanto se mantienen como "legado" y también se incluyen en reinicios manuales.
- El guardado diferido evita escrituras innecesarias; solo se escribe cuando hay cambios o al descargar el plugin.

## Formato de tiempo
Los mensajes de enfriamiento muestran tiempos legibles (por ejemplo `45s`, `3m 20s`, `2h 5m` o `1d 4h`).

## Instalación
1. Copia `FullDurabilityRepair.cs` en la carpeta `oxide/plugins/` del servidor.
2. Reinicia el servidor o usa el comando `oxide.reload FullDurabilityRepair`.
3. Concede permisos según sea necesario con `oxide.grant user <steamId> <permiso>` o `oxide.grant group <grupo> <permiso>`.
4. Ajusta la configuración en el archivo correspondiente y recarga el plugin si haces cambios.

## Créditos
Autor original: **Yersinia Pestis**

Mejoras de mantenimiento y documentación adicionales realizadas por la comunidad.
