#region Epoch Schema
namespace TrackScan;
internal sealed partial class ScanRepository
{
    private const string EpochSchema = """
        IF OBJECT_ID(N'dbo.ScanRailheadEpoch',N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.ScanRailheadEpoch(
                RunId bigint NOT NULL, Rail char(1) NOT NULL, Sequence int NOT NULL,
                PointID nvarchar(220) NOT NULL, UTCtimeStamp datetime2(0) NOT NULL,
                ScanHt float NULL, ScanPointCount bigint NOT NULL, SE float NULL, RejectedPointCount bigint NOT NULL,
                PolygonBatchId bigint NOT NULL, SourceFilePath nvarchar(2048) NOT NULL, SourceSha256 char(64) NOT NULL,
                FileBytes bigint NOT NULL, LasPointCount bigint NOT NULL,
                TimestampSource nvarchar(32) NOT NULL DEFAULT N'Manual UTC',
                CreatedUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT PK_ScanRailheadEpoch PRIMARY KEY(RunId,Rail,Sequence,UTCtimeStamp),
                CONSTRAINT FK_ScanRailheadEpochPoint FOREIGN KEY(RunId,Rail,Sequence)
                    REFERENCES dbo.ScanComputedPoint(RunId,Rail,Sequence) ON DELETE CASCADE,
                CONSTRAINT CK_ScanRailheadEpochCounts CHECK(ScanPointCount>=0 AND RejectedPointCount>=0),
                CONSTRAINT CK_ScanRailheadEpochValues CHECK(
                    (ScanPointCount=0 AND ScanHt IS NULL AND SE IS NULL) OR
                    (ScanPointCount=1 AND ScanHt IS NOT NULL AND SE IS NULL) OR
                    (ScanPointCount>1 AND ScanHt IS NOT NULL AND SE IS NOT NULL AND SE>=0)));
        END;
        IF COL_LENGTH(N'dbo.ScanRailheadEpoch',N'ScanOffsetUsed') IS NULL
            ALTER TABLE dbo.ScanRailheadEpoch ADD ScanOffsetUsed float NULL;
        IF COL_LENGTH(N'dbo.ScanRailheadEpoch',N'TimeZoneId') IS NULL
            ALTER TABLE dbo.ScanRailheadEpoch ADD TimeZoneId nvarchar(200) NULL;
        """;

}
#endregion
