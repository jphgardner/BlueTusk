BEGIN;
INSERT INTO fraud.accounts ("Id", "TenantId", "DisplayName", "CreatedAt")
VALUES
    ('30000000-0000-0000-0000-000000000001', 'pilot', 'Treasury account', CURRENT_TIMESTAMP),
    ('30000000-0000-0000-0000-000000000002', 'pilot', 'New supplier', CURRENT_TIMESTAMP),
    ('30000000-0000-0000-0000-000000000003', 'pilot', 'Settlement account', CURRENT_TIMESTAMP)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO fraud.transfers
    ("Id", "TenantId", "SourceId", "DestinationId", "Amount", "Currency", "RecordedAt")
VALUES
    ('31000000-0000-0000-0000-000000000001', 'pilot', '30000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000002', 25000.0000, 'GBP', CURRENT_TIMESTAMP),
    ('31000000-0000-0000-0000-000000000002', 'pilot', '30000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000003', 24950.0000, 'GBP', CURRENT_TIMESTAMP)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO fraud.alert_rules
    ("Id", "TenantId", "Name", "MinimumAmount", "Enabled", "CreatedAt")
VALUES
    ('31500000-0000-0000-0000-000000000001', 'pilot', 'High-value multi-hop path', 10000.0000, TRUE, CURRENT_TIMESTAMP)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO fraud.investigation_cases
    ("Id", "TenantId", "Reason", "Assignee", "Decision", "DecisionNote", "Version", "OpenedAt", "DecidedAt")
VALUES
    ('32000000-0000-0000-0000-000000000001', 'pilot', 'Rapid transfer fan-out', NULL, 0, NULL, 1, CURRENT_TIMESTAMP, NULL)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO fraud.evidence_audit
    ("TenantId", "CaseId", "Operation", "Actor", "Detail", "RecordedAt")
SELECT 'pilot', '32000000-0000-0000-0000-000000000001', 'case.opened', 'rc-seed', 'Rapid transfer fan-out', CURRENT_TIMESTAMP
WHERE NOT EXISTS (
    SELECT 1 FROM fraud.evidence_audit
    WHERE "TenantId" = 'pilot'
      AND "CaseId" = '32000000-0000-0000-0000-000000000001'
      AND "Operation" = 'case.opened'
      AND "Actor" = 'rc-seed');
COMMIT;
