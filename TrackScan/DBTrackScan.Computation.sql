-- Revision 067. Additive provisional schema 2 -> 3, within the schema transaction.
IF (SELECT COUNT(*) FROM dbo.ScanSchemaVersion WHERE VersionNumber=2) <> 1
    THROW 51067, 'Expected provisional scan schema version 2.', 1;

ALTER TABLE dbo.ScanTrack ADD PointSpacing decimal(4,2) NOT NULL
    CONSTRAINT DF_ScanTrackSpacing DEFAULT 3.00 WITH VALUES;
EXEC(N'ALTER TABLE dbo.ScanTrack ADD CONSTRAINT CK_ScanTrackSpacing CHECK (PointSpacing IN (1.00,3.00));');
ALTER TABLE dbo.ScanTrack ADD CONSTRAINT UQ_ScanTrackProjectId UNIQUE (ScanProjectId,TrackId);

CREATE TABLE dbo.ScanComputedRun
(
    RunId bigint IDENTITY NOT NULL CONSTRAINT PK_ScanComputedRun PRIMARY KEY,
    ScanProjectId int NOT NULL,
    TrackId int NOT NULL,
    SurveyId bigint NOT NULL,
    SourcePreparationRevision bigint NOT NULL,
    TrackDefinition nvarchar(max) NOT NULL,
    PointCount int NOT NULL,
    IsCurrent bit NOT NULL,
    ComputedUtc datetime2(7) NOT NULL CONSTRAINT DF_ScanComputedUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_ScanComputedTrack FOREIGN KEY (ScanProjectId,TrackId) REFERENCES dbo.ScanTrack(ScanProjectId,TrackId),
    CONSTRAINT FK_ScanComputedSurvey FOREIGN KEY (ScanProjectId,SurveyId) REFERENCES dbo.ScanReferenceSurvey(ScanProjectId,SurveyId),
    CONSTRAINT CK_ScanComputedCount CHECK (PointCount >= 4)
);
CREATE UNIQUE INDEX UX_ScanComputedCurrent ON dbo.ScanComputedRun(TrackId) WHERE IsCurrent=1;

CREATE TABLE dbo.ScanComputedPoint
(
    RunId bigint NOT NULL CONSTRAINT FK_ScanComputedPointRun REFERENCES dbo.ScanComputedRun(RunId),
    Rail char(1) NOT NULL,
    Sequence int NOT NULL,
    PointName nvarchar(220) NOT NULL,
    Easting decimal(24,4) NOT NULL,
    Northing decimal(24,4) NOT NULL,
    TopOfRailHeight decimal(24,4) NOT NULL,
    DistanceAlongRail decimal(24,4) NOT NULL,
    Chainage decimal(24,4) NULL,
    SurveyPointCount int NOT NULL,
    HeightInterpolated bit NOT NULL,
    CONSTRAINT PK_ScanComputedPoint PRIMARY KEY (RunId,Rail,Sequence),
    CONSTRAINT CK_ScanComputedRail CHECK (Rail IN ('L','R')),
    CONSTRAINT CK_ScanComputedSequence CHECK (Sequence > 0),
    CONSTRAINT CK_ScanComputedSamples CHECK (SurveyPointCount >= 0)
);
ALTER TABLE dbo.ScanSchemaVersion DROP CONSTRAINT CK_ScanSchemaVersion;
UPDATE dbo.ScanSchemaVersion SET VersionNumber=3;
ALTER TABLE dbo.ScanSchemaVersion ADD CONSTRAINT CK_ScanSchemaVersion CHECK (VersionNumber=3);
