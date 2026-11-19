/** Configuracion de desarrollo. En produccion (docker/nginx) se reemplaza por variables de entorno del build. */
export const CONFIG = {
  apiUrl: 'http://localhost:5001',
  keycloak: { url: 'http://localhost:8081', realm: 'mercadoya', clientId: 'web' },
};
