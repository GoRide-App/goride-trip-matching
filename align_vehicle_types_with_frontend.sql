-- GoRide Trip & Matching — align vehicle_types with the frontend's 4 known
-- vehicle types (BIKE, TUK, CAR, XL) and their mock ids (vt_bike, vt_tuk,
-- vt_car, vt_xl), so SelectVehicleSheet's join on vehicleTypeId actually
-- matches the real backend fare estimate instead of falling through to "—".
--
-- Run against trip_db. Safe to re-run: each UPDATE targets the *old* id, so
-- once applied once, a second run matches 0 rows and is a no-op.
--
-- Precondition checked before writing this: `trips` has 0 rows right now, so
-- changing these primary keys cannot orphan any fk_trips_vehicle_type
-- reference. If trips has since gained rows, STOP and re-check first —
-- this script does not touch the trips table.

USE trip_db;

START TRANSACTION;

-- TUKTUK -> TUK (matches frontend's VehicleTypeCode "TUK"; keeps existing
-- real fare rates and active=TRUE — this is the only bookable vehicle type
-- in this stage).
UPDATE vehicle_types
SET id = 'vt_tuk',
    code = 'TUK'
WHERE id = '11111111-1111-1111-1111-111111111111';

-- CAR: id only (code already matches frontend's "CAR"); rates and
-- active=FALSE unchanged.
UPDATE vehicle_types
SET id = 'vt_car'
WHERE id = '22222222-2222-2222-2222-222222222222';

-- VAN -> BIKE (frontend has no VAN concept; repurpose the row with
-- frontend's mock BIKE rates). Stays active=FALSE — not bookable yet.
UPDATE vehicle_types
SET id = 'vt_bike',
    code = 'BIKE',
    capacity = 1,
    base_fare = 60.00,
    rate_per_km = 42.00,
    rate_per_min = 3.00
WHERE id = '33333333-3333-3333-3333-333333333333';

-- LORRY -> XL (frontend has no LORRY concept; repurpose the row with
-- frontend's mock XL rates). Stays active=FALSE — not bookable yet.
UPDATE vehicle_types
SET id = 'vt_xl',
    code = 'XL',
    capacity = 6,
    base_fare = 260.00,
    rate_per_km = 145.00,
    rate_per_min = 8.00
WHERE id = '44444444-4444-4444-4444-444444444444';

COMMIT;

-- Verify the result:
SELECT id, code, capacity, base_fare, rate_per_km, rate_per_min, active
FROM vehicle_types
ORDER BY active DESC, base_fare ASC;
