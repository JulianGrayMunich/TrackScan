#region Hourly And Daily Tables For Fresh Database Creation
namespace TrackScan;

internal sealed partial class ScanRepository
{
    // Future TrackScanScheduler writes these summaries. No reduction is run here.
    // Keys match ScanRailheadEpoch and retain computed-run/project provenance.
    private const string ReductionCreationSchema = """
        CREATE TABLE dbo.ScanRailheadHour
        (
            RunId bigint NOT NULL,
            Rail char(1) NOT NULL,
            Sequence int NOT NULL,
            PointID nvarchar(220) NOT NULL,
            -- End of the UTC hour: [UTCtimeStamp - 1 hour, UTCtimeStamp).
            UTCtimeStamp datetime2(0) NOT NULL,
            ScanHt float NULL,
            -- Sum of ScanPointCount for valid contributing epoch readings.
            ScanPointCount bigint NOT NULL,
            -- Number of valid contributing epoch readings; not LAS point count.
            EpochCount bigint NOT NULL,
            ComputedUtc datetime2(7) NOT NULL
                CONSTRAINT DF_ScanRailheadHourComputedUtc DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_ScanRailheadHour PRIMARY KEY(RunId,Rail,Sequence,UTCtimeStamp),
            CONSTRAINT FK_ScanRailheadHourPoint FOREIGN KEY(RunId,Rail,Sequence)
                REFERENCES dbo.ScanComputedPoint(RunId,Rail,Sequence) ON DELETE CASCADE,
            CONSTRAINT CK_ScanRailheadHourBoundary CHECK
                (DATEPART(MINUTE,UTCtimeStamp)=0 AND DATEPART(SECOND,UTCtimeStamp)=0),
            CONSTRAINT CK_ScanRailheadHourValues CHECK
                ((EpochCount=0 AND ScanPointCount=0 AND ScanHt IS NULL) OR
                 (EpochCount>0 AND ScanPointCount>=EpochCount AND ScanHt IS NOT NULL))
        );

        CREATE TABLE dbo.ScanRailheadDay
        (
            RunId bigint NOT NULL,
            Rail char(1) NOT NULL,
            Sequence int NOT NULL,
            PointID nvarchar(220) NOT NULL,
            -- 12:00 UTC on the represented day; this is a label, not a boundary.
            UTCtimeStamp datetime2(0) NOT NULL,
            ScanHt float NULL,
            -- Ordinary mean of valid hourly values; never weighted by point count.
            HourCount tinyint NOT NULL,
            ComputedUtc datetime2(7) NOT NULL
                CONSTRAINT DF_ScanRailheadDayComputedUtc DEFAULT SYSUTCDATETIME(),
            CONSTRAINT PK_ScanRailheadDay PRIMARY KEY(RunId,Rail,Sequence,UTCtimeStamp),
            CONSTRAINT FK_ScanRailheadDayPoint FOREIGN KEY(RunId,Rail,Sequence)
                REFERENCES dbo.ScanComputedPoint(RunId,Rail,Sequence) ON DELETE CASCADE,
            CONSTRAINT CK_ScanRailheadDayBoundary CHECK
                (DATEPART(HOUR,UTCtimeStamp)=12 AND DATEPART(MINUTE,UTCtimeStamp)=0 AND DATEPART(SECOND,UTCtimeStamp)=0),
            CONSTRAINT CK_ScanRailheadDayValues CHECK
                ((HourCount=0 AND ScanHt IS NULL) OR
                 (HourCount BETWEEN 1 AND 24 AND ScanHt IS NOT NULL))
        );
        """;
}
#endregion
