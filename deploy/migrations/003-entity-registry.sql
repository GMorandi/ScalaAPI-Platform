-- Product-owned discovery registry. Business listings must not inspect Orleans
-- persistence payloads; grains remain the runtime state owner while this table
-- owns durable identity and list membership.
CREATE TABLE entity_registry (
    entity_type text NOT NULL,
    entity_key text NOT NULL,
    entity_id bigint,
    status text NOT NULL DEFAULT 'active',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_type, entity_key)
);

CREATE UNIQUE INDEX ux_entity_registry_numeric_id
    ON entity_registry(entity_type, entity_id)
    WHERE entity_id IS NOT NULL;
CREATE INDEX ix_entity_registry_type_numeric
    ON entity_registry(entity_type, entity_id)
    WHERE entity_id IS NOT NULL;
CREATE INDEX ix_entity_registry_type_key
    ON entity_registry(entity_type, entity_key);
