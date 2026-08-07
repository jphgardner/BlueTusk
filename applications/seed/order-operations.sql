BEGIN;
INSERT INTO orders.fulfilment_orders
    ("Id", "TenantId", "CustomerReference", "State", "AllocationReference", "Version", "CreatedAt", "UpdatedAt")
VALUES
    ('10000000-0000-0000-0000-000000000001', 'pilot', 'PILOT-ORDER-001', 0, NULL, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
    ('10000000-0000-0000-0000-000000000002', 'pilot', 'PILOT-ORDER-002', 1, 'ZONE-A', 2, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO orders.operational_audit
    ("TenantId", "AggregateId", "Operation", "IdempotencyKey", "Payload", "RecordedAt", "RelayedAt")
VALUES
    ('pilot', '10000000-0000-0000-0000-000000000001', 'seed', 'rc-seed-order-001', '{}', CURRENT_TIMESTAMP, NULL),
    ('pilot', '10000000-0000-0000-0000-000000000002', 'seed', 'rc-seed-order-002', '{}', CURRENT_TIMESTAMP, NULL)
ON CONFLICT ("TenantId", "IdempotencyKey") DO NOTHING;
COMMIT;
