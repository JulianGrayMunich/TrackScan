-- Revision 065: provisional preparation schema only. Execute inside the repository's schema transaction.
IF OBJECT_ID(N'dbo.ScanSchemaVersion', N'U') IS NOT NULL
BEGIN
    IF (SELECT COUNT(*) FROM dbo.ScanSchemaVersion WHERE VersionNumber = 2) <> 1
        THROW 51063, 'Unsupported DBTrackScan schema version.', 1;
    RETURN;
END;
IF EXISTS (SELECT 1 FROM sys.tables WHERE is_ms_shipped = 0)
    THROW 51063, 'DBTrackScan contains unrecognised tables. No changes were made.', 1;

CREATE TABLE dbo.ScanSchemaVersion
(
    VersionNumber int NOT NULL CONSTRAINT PK_ScanSchemaVersion PRIMARY KEY,
    InstalledUtc datetime2(7) NOT NULL CONSTRAINT DF_ScanSchemaInstalled DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ScanSchemaVersion CHECK (VersionNumber = 2)
);

CREATE TABLE dbo.ScanProject
(
    ScanProjectId int IDENTITY NOT NULL CONSTRAINT PK_ScanProject PRIMARY KEY,
    GeometryProjectId int NOT NULL,
    GeometryDatabaseCreated datetime2(3) NOT NULL,
    ProjectName nvarchar(200) NOT NULL,
    PreparationRevision bigint NOT NULL CONSTRAINT DF_ScanProjectRevision DEFAULT 0,
    CurrentSurveyId bigint NULL,
    ModifiedUtc datetime2(7) NOT NULL CONSTRAINT DF_ScanProjectModified DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_ScanProjectGeometry UNIQUE (GeometryDatabaseCreated, GeometryProjectId)
);

CREATE TABLE dbo.ScanReferenceSurvey
(
    SurveyId bigint IDENTITY NOT NULL CONSTRAINT PK_ScanReferenceSurvey PRIMARY KEY,
    ScanProjectId int NOT NULL CONSTRAINT FK_ScanSurveyProject REFERENCES dbo.ScanProject(ScanProjectId),
    SourceFileName nvarchar(260) NOT NULL,
    Sha256 char(64) NOT NULL,
    IncludesHeader bit NOT NULL,
    PointCount bigint NOT NULL,
    ImportedUtc datetime2(7) NOT NULL CONSTRAINT DF_ScanSurveyImported DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ScanSurveyCount CHECK (PointCount > 0),
    CONSTRAINT UQ_ScanSurveyProjectId UNIQUE (ScanProjectId, SurveyId),
    CONSTRAINT UQ_ScanSurveyContent UNIQUE (ScanProjectId, Sha256, IncludesHeader)
);
ALTER TABLE dbo.ScanProject ADD CONSTRAINT FK_ScanProjectCurrentSurvey
    FOREIGN KEY (ScanProjectId, CurrentSurveyId) REFERENCES dbo.ScanReferenceSurvey(ScanProjectId, SurveyId);

CREATE TABLE dbo.ScanReferenceSurveyPoint
(
    SurveyId bigint NOT NULL CONSTRAINT FK_ScanPointSurvey REFERENCES dbo.ScanReferenceSurvey(SurveyId),
    SourceRecord bigint NOT NULL,
    PointName nvarchar(256) NOT NULL,
    Easting decimal(24,4) NOT NULL,
    Northing decimal(24,4) NOT NULL,
    TopOfRailHeight decimal(24,4) NOT NULL,
    Chainage decimal(24,4) NULL,
    CONSTRAINT PK_ScanReferenceSurveyPoint PRIMARY KEY (SurveyId, SourceRecord)
);

CREATE TABLE dbo.ScanTrack
(
    TrackId int IDENTITY NOT NULL CONSTRAINT PK_ScanTrack PRIMARY KEY,
    ScanProjectId int NOT NULL CONSTRAINT FK_ScanTrackProject REFERENCES dbo.ScanProject(ScanProjectId),
    TrackSlot tinyint NOT NULL,
    TrackName nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL,
    GaugeMillimetres int NOT NULL CONSTRAINT DF_ScanTrackGauge DEFAULT 1600,
    LeftStartE decimal(24,4) NOT NULL,
    LeftStartN decimal(24,4) NOT NULL,
    RightStartE decimal(24,4) NOT NULL,
    RightStartN decimal(24,4) NOT NULL,
    LeftEndE decimal(24,4) NOT NULL,
    LeftEndN decimal(24,4) NOT NULL,
    RightEndE decimal(24,4) NOT NULL,
    RightEndN decimal(24,4) NOT NULL,
    StartChainage decimal(24,4) NULL,
    EndChainage decimal(24,4) NULL,
    IsDeleted bit NOT NULL CONSTRAINT DF_ScanTrackDeleted DEFAULT 0,
    ModifiedUtc datetime2(7) NOT NULL CONSTRAINT DF_ScanTrackModified DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ScanTrackGauge CHECK (GaugeMillimetres IN (1435,1600)),
    CONSTRAINT CK_ScanTrackSlot CHECK (TrackSlot BETWEEN 1 AND 6),
    CONSTRAINT CK_ScanTrackName CHECK (LEN(LTRIM(RTRIM(TrackName))) > 0),
    CONSTRAINT CK_ScanTrackLeft CHECK (LeftStartE <> LeftEndE OR LeftStartN <> LeftEndN),
    CONSTRAINT CK_ScanTrackRight CHECK (RightStartE <> RightEndE OR RightStartN <> RightEndN),
    CONSTRAINT CK_ScanTrackStart CHECK (LeftStartE <> RightStartE OR LeftStartN <> RightStartN),
    CONSTRAINT CK_ScanTrackEnd CHECK (LeftEndE <> RightEndE OR LeftEndN <> RightEndN)
);
CREATE UNIQUE INDEX UX_ScanTrackSlot ON dbo.ScanTrack(ScanProjectId, TrackSlot) WHERE IsDeleted = 0;
CREATE UNIQUE INDEX UX_ScanTrackName ON dbo.ScanTrack(ScanProjectId, TrackName) WHERE IsDeleted = 0;

CREATE TABLE dbo.ScanPreparationChange
(
    ChangeId bigint IDENTITY NOT NULL CONSTRAINT PK_ScanPreparationChange PRIMARY KEY,
    ScanProjectId int NOT NULL CONSTRAINT FK_ScanChangeProject REFERENCES dbo.ScanProject(ScanProjectId),
    PreparationRevision bigint NOT NULL,
    ChangeKind nvarchar(40) NOT NULL,
    Details nvarchar(max) NOT NULL,
    ChangedUtc datetime2(7) NOT NULL CONSTRAINT DF_ScanChangeUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_ScanChangeRevision UNIQUE (ScanProjectId, PreparationRevision)
);
INSERT dbo.ScanSchemaVersion(VersionNumber) VALUES(2);
