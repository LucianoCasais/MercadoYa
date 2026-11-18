-- Se ejecuta una sola vez, al crear el volumen. La BD `mercadoya` la crea la imagen (POSTGRES_DB).
CREATE DATABASE payments;   -- base propia del servicio de pagos
CREATE DATABASE keycloak;   -- almacenamiento de Keycloak
