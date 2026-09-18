-- GoRide Trip & Matching — driver_offers
--
-- Tracks which drivers a trip request was offered to (one row per trip+driver)
-- and what each driver did with it. Status: Pending, Accepted, Declined,
-- Expired, Failed.
--
-- Run this against trip_db using the Azure MySQL server's ADMIN login — the
-- service's own user (trip_svc) is scoped to trip_db and normally can't create
-- tables. The app also tries this DDL on startup
-- (DriverOfferRepository.EnsureSchemaAsync) but that only works if trip_svc has
-- CREATE rights; otherwise it logs "Failed to ensure driver_offers schema exists"
-- and carries on, so running this by hand is the reliable path.
--
-- trip_svc needs SELECT, INSERT and UPDATE on driver_offers afterwards (it
-- already has them if it was granted rights on all of trip_db).
--
-- Safe to re-run.
--
-- trip_id deliberately has no foreign key to `trips`: nothing in this service
-- owns that table's schema yet.

USE trip_db;

CREATE TABLE IF NOT EXISTS driver_offers (
    trip_id     VARCHAR(64) NOT NULL,
    driver_id   VARCHAR(64) NOT NULL,
    rider_id    VARCHAR(64) NOT NULL,
    status      VARCHAR(16) NOT NULL DEFAULT 'Pending',
    distance_km DOUBLE      NOT NULL,
    created_at  DATETIME(3) NOT NULL,
    decided_at  DATETIME(3) NULL,
    PRIMARY KEY (trip_id, driver_id),
    INDEX idx_driver_offers_driver_status (driver_id, status)
);
