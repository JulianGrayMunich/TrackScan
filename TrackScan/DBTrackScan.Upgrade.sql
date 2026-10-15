-- Revision 065 compatibility upgrade of the provisional schema.
-- Executed under the repository schema transaction and exclusive application lock.
-- No tables or survey imports are deleted; coordinate values are rounded as requested.
IF (SELECT COUNT(*) FROM dbo.ScanSchemaVersion WHERE VersionNumber=1) <> 1
    THROW 51065, 'Expected provisional scan schema version 1.', 1;

ALTER TABLE dbo.ScanTrack ADD GaugeMillimetres int NOT NULL
    CONSTRAINT DF_ScanTrackGauge DEFAULT 1435 WITH VALUES;
-- Bind the constraint only after SQL Server has added the new column.
EXEC(N'ALTER TABLE dbo.ScanTrack ADD CONSTRAINT CK_ScanTrackGauge CHECK (GaugeMillimetres IN (1435,1600));');
ALTER TABLE dbo.ScanTrack DROP CONSTRAINT CK_ScanTrackLeft, CK_ScanTrackRight, CK_ScanTrackStart, CK_ScanTrackEnd;
UPDATE dbo.ScanReferenceSurveyPoint SET Easting=ROUND(Easting,4);
ALTER TABLE dbo.ScanReferenceSurveyPoint ALTER COLUMN Easting decimal(24,4) NOT NULL;
UPDATE dbo.ScanReferenceSurveyPoint SET Northing=ROUND(Northing,4);
ALTER TABLE dbo.ScanReferenceSurveyPoint ALTER COLUMN Northing decimal(24,4) NOT NULL;
UPDATE dbo.ScanReferenceSurveyPoint SET TopOfRailHeight=ROUND(TopOfRailHeight,4);
ALTER TABLE dbo.ScanReferenceSurveyPoint ALTER COLUMN TopOfRailHeight decimal(24,4) NOT NULL;
UPDATE dbo.ScanReferenceSurveyPoint SET Chainage=ROUND(Chainage,4);
ALTER TABLE dbo.ScanReferenceSurveyPoint ALTER COLUMN Chainage decimal(24,4) NULL;
UPDATE dbo.ScanTrack SET LeftStartE=ROUND(LeftStartE,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN LeftStartE decimal(24,4) NOT NULL;
UPDATE dbo.ScanTrack SET LeftStartN=ROUND(LeftStartN,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN LeftStartN decimal(24,4) NOT NULL;
UPDATE dbo.ScanTrack SET RightStartE=ROUND(RightStartE,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN RightStartE decimal(24,4) NOT NULL;
UPDATE dbo.ScanTrack SET RightStartN=ROUND(RightStartN,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN RightStartN decimal(24,4) NOT NULL;
UPDATE dbo.ScanTrack SET LeftEndE=ROUND(LeftEndE,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN LeftEndE decimal(24,4) NOT NULL;
UPDATE dbo.ScanTrack SET LeftEndN=ROUND(LeftEndN,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN LeftEndN decimal(24,4) NOT NULL;
UPDATE dbo.ScanTrack SET RightEndE=ROUND(RightEndE,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN RightEndE decimal(24,4) NOT NULL;
UPDATE dbo.ScanTrack SET RightEndN=ROUND(RightEndN,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN RightEndN decimal(24,4) NOT NULL;
UPDATE dbo.ScanTrack SET StartChainage=ROUND(StartChainage,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN StartChainage decimal(24,4) NULL;
UPDATE dbo.ScanTrack SET EndChainage=ROUND(EndChainage,4);
ALTER TABLE dbo.ScanTrack ALTER COLUMN EndChainage decimal(24,4) NULL;

ALTER TABLE dbo.ScanTrack ADD CONSTRAINT CK_ScanTrackLeft CHECK (LeftStartE <> LeftEndE OR LeftStartN <> LeftEndN);
ALTER TABLE dbo.ScanTrack ADD CONSTRAINT CK_ScanTrackRight CHECK (RightStartE <> RightEndE OR RightStartN <> RightEndN);
ALTER TABLE dbo.ScanTrack ADD CONSTRAINT CK_ScanTrackStart CHECK (LeftStartE <> RightStartE OR LeftStartN <> RightStartN);
ALTER TABLE dbo.ScanTrack ADD CONSTRAINT CK_ScanTrackEnd CHECK (LeftEndE <> RightEndE OR LeftEndN <> RightEndN);
UPDATE dbo.ScanProject SET PreparationRevision=PreparationRevision+1, ModifiedUtc=SYSUTCDATETIME();
INSERT dbo.ScanPreparationChange(ScanProjectId,PreparationRevision,ChangeKind,Details)
    SELECT ScanProjectId,PreparationRevision,N'Coordinate precision',
        N'Provisional schema updated to four-decimal coordinate storage. Gauge defaults to 1435 mm; rail adjustment occurs on saving a track.'
    FROM dbo.ScanProject;
ALTER TABLE dbo.ScanSchemaVersion DROP CONSTRAINT CK_ScanSchemaVersion;
UPDATE dbo.ScanSchemaVersion SET VersionNumber=2;
ALTER TABLE dbo.ScanSchemaVersion ADD CONSTRAINT CK_ScanSchemaVersion CHECK (VersionNumber=2);
