-- BD mercadoya: esquemas `catalogo` (Catalogo + Inventario) y `pedidos` (Pedidos).
-- Importes en centavos (bigint). Los modulos se vinculan por identificadores.
-- La concurrencia optimista de Product usa la columna de sistema xmin (EF Core / Npgsql).
CREATE SCHEMA IF NOT EXISTS catalogo;
CREATE SCHEMA IF NOT EXISTS pedidos;

-- Arbol de categorias (Categoria > Subcategoria > Hoja) para el menu del catalogo.
-- "ParentId" NULL = categoria raiz. Los productos apuntan a cualquier nodo (normalmente una hoja).
CREATE TABLE catalogo."Category" (
  "Id"       integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  "Name"     varchar(80) NOT NULL,
  "ParentId" integer REFERENCES catalogo."Category"("Id"),
  CHECK ("ParentId" IS NULL OR "ParentId" <> "Id")
);
CREATE UNIQUE INDEX ux_category_parent_name ON catalogo."Category" (COALESCE("ParentId", 0), "Name");
CREATE INDEX ix_category_parent ON catalogo."Category" ("ParentId");

CREATE TABLE catalogo."Product" (
  "Id"          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  "Name"        varchar(150) NOT NULL,
  "Description" text NOT NULL DEFAULT '',
  "PriceCents"  bigint NOT NULL CHECK ("PriceCents" > 0),
  "Stock"       integer NOT NULL CHECK ("Stock" >= 0),   -- nunca negativo: respalda la reserva atomica
  "CategoryId"  integer NOT NULL REFERENCES catalogo."Category"("Id"),
  "ImageUrl"    varchar(500),
  "Active"      boolean NOT NULL DEFAULT true            -- baja logica
);
CREATE INDEX ix_product_name_lower ON catalogo."Product" (lower("Name"));
CREATE INDEX ix_product_category   ON catalogo."Product" ("CategoryId") WHERE "Active";

CREATE TABLE catalogo."StockMovement" (
  "Id"        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  "ProductId" integer NOT NULL REFERENCES catalogo."Product"("Id"),
  "Delta"     integer NOT NULL CHECK ("Delta" <> 0),
  "Reason"    varchar(20) NOT NULL CHECK ("Reason" IN ('Inicial','Ajuste','Venta','Liberacion','Cancelacion')),
  "OrderId"   uuid,
  "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_stockmovement_product ON catalogo."StockMovement" ("ProductId", "CreatedAt" DESC);

CREATE TABLE catalogo."StockReservation" (
  "OrderId"   uuid NOT NULL,
  "ProductId" integer NOT NULL REFERENCES catalogo."Product"("Id"),
  "Quantity"  integer NOT NULL CHECK ("Quantity" > 0),
  "Status"    varchar(10) NOT NULL CHECK ("Status" IN ('Reservada','Liberada')),
  "CreatedAt" timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY ("OrderId", "ProductId")                   -- idempotencia por pedido y producto
);

CREATE TABLE pedidos."Order" (
  "Id"             uuid PRIMARY KEY,
  "UserId"         varchar(64) NOT NULL,                 -- claim `sub` de Keycloak
  "BuyerName"      varchar(150) NOT NULL DEFAULT '',      -- nombre de quien compra, copiado del token al comprar
  "Status"         varchar(12) NOT NULL CHECK ("Status" IN ('Pendiente','Confirmado','Rechazado','Entregado','Cancelado')),
  "Reason"         varchar(20) CHECK ("Reason" IN ('SinStock','StockNoDisponible','PagoFallido','PagoNoDisponible','BrokerNoDisponible')),
  "TotalCents"     bigint NOT NULL CHECK ("TotalCents" >= 0),
  "IdempotencyKey" varchar(100) NOT NULL,
  "CreatedAt"      timestamptz NOT NULL DEFAULT now(),
  "UpdatedAt"      timestamptz NOT NULL DEFAULT now(),
  UNIQUE ("UserId", "IdempotencyKey"),                   -- evita pedidos duplicados por doble clic
  CHECK (("Status" = 'Rechazado') = ("Reason" IS NOT NULL))
);
CREATE INDEX ix_order_user   ON pedidos."Order" ("UserId", "CreatedAt" DESC);
CREATE INDEX ix_order_status ON pedidos."Order" ("Status", "CreatedAt" DESC);

CREATE TABLE pedidos."OrderItem" (
  "OrderId"        uuid NOT NULL REFERENCES pedidos."Order"("Id") ON DELETE CASCADE,
  "ProductId"      integer NOT NULL,                      -- solo identificador: sin FK entre modulos
  "ProductName"    varchar(150) NOT NULL,                 -- precio y nombre congelados al comprar
  "UnitPriceCents" bigint NOT NULL CHECK ("UnitPriceCents" > 0),
  "Quantity"       integer NOT NULL CHECK ("Quantity" > 0),
  PRIMARY KEY ("OrderId", "ProductId")
);
