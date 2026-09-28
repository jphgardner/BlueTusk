-- Run explicitly on a development PostgreSQL database with wal_level=logical.
-- Every source column is text so the sample's pgoutput decoder uses full text images.
CREATE SCHEMA IF NOT EXISTS orders_sample;
CREATE TABLE IF NOT EXISTS orders_sample.customers(id text NOT NULL, tenant text NOT NULL, name text NOT NULL, PRIMARY KEY(tenant,id));
CREATE TABLE IF NOT EXISTS orders_sample.orders(id text NOT NULL, tenant text NOT NULL, customer text NOT NULL, amount text NOT NULL, PRIMARY KEY(tenant,id));
ALTER TABLE orders_sample.customers REPLICA IDENTITY FULL;
ALTER TABLE orders_sample.orders REPLICA IDENTITY FULL;
INSERT INTO orders_sample.customers VALUES('customer','first','Alice') ON CONFLICT DO NOTHING;
INSERT INTO orders_sample.orders VALUES('1','first','customer','10') ON CONFLICT DO NOTHING;
CREATE PUBLICATION orders_live_sample FOR TABLE orders_sample.customers, orders_sample.orders;
