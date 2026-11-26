/**
 * Medicion del indicador del acta "catalogo en menos de 3 s": cuanto tarda el catalogo en verse con productos desde que se
 * abre la pagina. performance.now() cuenta desde el inicio de la navegacion del documento, asi que incluye la descarga del
 * HTML y del JS, el arranque de Angular, la consulta de sesion a Keycloak y la respuesta de la API.
 *
 * Se mide una sola vez y solo si la pagina se abrio directamente en el catalogo (/ o /tienda). El resultado queda como
 * performance.mark('catalogo-visible') (lo lee tests/e2e/medir_carga.mjs) y en la consola del navegador.
 */
export const CATALOG_TARGET_MS = 3000;

let measured = false;

export function markCatalogVisible(): number | null {
  if (measured || typeof performance === 'undefined') return null;
  measured = true;
  const nav = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined;
  const path = nav ? new URL(nav.name).pathname : location.pathname;
  if (path !== '/' && path !== '/tienda') return null;   // se entro por otra pantalla: no es la carga del catalogo
  const ms = Math.round(performance.now());
  performance.mark('catalogo-visible', { detail: { ms } });
  console.info(`[MercadoYa] Catálogo visible en ${ms} ms desde que se abrió la página (objetivo del acta: menos de ${CATALOG_TARGET_MS} ms).`);
  return ms;
}
