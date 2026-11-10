-- Model outcomes: always check before consuming the data.
SELECT path, status, element_count FROM models ORDER BY model_id;

-- Instance totals by category and type across models.
SELECT m.path, e.category, e.family, e.type_name, COUNT(*) AS instances
FROM elements e JOIN models m USING (model_id)
WHERE e.kind = 'instance'
GROUP BY e.model_id, e.category, e.family, e.type_name;

-- Search values; replace the sample value as needed.
SELECT m.path, e.element_id, e.family, e.type_name, p.scope, p.name, p.display_value
FROM parameters p
JOIN elements e USING (model_id, element_id)
JOIN models m USING (model_id)
WHERE p.name = 'Fire Rating' AND p.display_value LIKE '%60%';

-- Missing or empty Mark on exported instances (export Mark first).
SELECT m.path, e.element_id, e.category, e.family, e.type_name
FROM elements e JOIN models m USING (model_id)
WHERE e.kind = 'instance' AND NOT EXISTS (
  SELECT 1 FROM parameters p
  WHERE p.model_id=e.model_id AND p.element_id=e.element_id
    AND p.scope='instance' AND p.parameter_key='builtin:ALL_MODEL_MARK'
    AND p.has_value=1 AND TRIM(p.raw_value) <> ''
);
