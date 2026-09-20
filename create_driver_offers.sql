-- GoRide Trip & Matching — driver_offers
--
-- Tracks which drivers a trip request was offered to (one row per trip+driver)
-- and what each driver did with it. Status: Pending, Accepted, Declined,
-- Expired, Failed.
--
-- Run this against trip_db using the Azure MySQL server's ADMIN login — the
-- service's own user (trip_svc) is scoped to trip_db and normally can't create
-- tables. The app also tries to create the table on startup
-- (DriverOfferRepository.EnsureSchemaAsync), but that is CREATE IF NOT EXISTS:
-- it does nothing if a table with this name already exists, even one with a
-- different layout. So an older driver_offers must be moved aside first (step 1).
--
-- trip_id deliberately has no foreign key to `trips` and is VARCHAR(64): the
-- frontend's trip ids ("trp_" + a UUID) are 40 characters, longer than char(36).

USE trip_db;

-- ---------------------------------------------------------------------------
-- 1. If driver_offers ALREADY EXISTS with the older layout (columns id, trip_id,
--    driver_id, round, offered_at, responded_at, response), move it aside. This
--    keeps its data; nothing is dropped. Skip this step on a brand-new database.
--    Drop the old copy yourself later:  DROP TABLE driver_offers_old;
-- ---------------------------------------------------------------------------
-- RENAME TABLE driver_offers TO driver_offers_old;

-- ---------------------------------------------------------------------------
-- 2. Create the table the service uses.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS driver_offers (
    trip_id          VARCHAR(64)   NOT NULL,
    driver_id        VARCHAR(64)   NOT NULL,
    rider_id         VARCHAR(64)   NOT NULL,
    status           VARCHAR(16)   NOT NULL DEFAULT 'Pending',
    distance_km      DOUBLE        NOT NULL,
    pickup_location  VARCHAR(255)  NULL,
    dropoff_location VARCHAR(255)  NULL,
    fare             DECIMAL(10,2) NULL,
    created_at       DATETIME(3)   NOT NULL,
    decided_at       DATETIME(3)   NULL,
    PRIMARY KEY (trip_id, driver_id),
    INDEX idx_driver_offers_driver_status (driver_id, status)
);

-- ---------------------------------------------------------------------------
-- 3. Only if trip_svc has rights per table rather than on all of trip_db:
-- ---------------------------------------------------------------------------
-- GRANT SELECT, INSERT, UPDATE ON trip_db.driver_offers TO 'trip_svc'@'%';
