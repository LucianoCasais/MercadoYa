-- BD propia del servicio de pagos (database per service).
\connect payments

CREATE TABLE "PaymentRecord" (
  "Id"          uuid PRIMARY KEY,
  "OrderId"     uuid NOT NULL UNIQUE,                    -- cobro unico por pedido: idempotencia
  "AmountCents" bigint NOT NULL CHECK ("AmountCents" > 0),
  "Status"      varchar(10) NOT NULL CHECK ("Status" IN ('Aprobado','Rechazado')),
  "Reason"      varchar(40),
  "CreatedAt"   timestamptz NOT NULL DEFAULT now()
);
