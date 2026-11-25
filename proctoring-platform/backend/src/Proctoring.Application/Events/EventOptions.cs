using System.ComponentModel.DataAnnotations;

namespace Proctoring.Application.Events;

/// <summary>Audio event upload and maintenance settings (environment: AUDIO_*, EVENT_*).</summary>
public sealed class AudioEventOptions
{
    // Required stream format (fixed by the contract: 16 kHz, mono, 16-bit PCM).
    public const int RequiredSampleRate = 16000;
    public const int RequiredChannels = 1;
    public const int RequiredBitsPerSample = 16;

    /// <summary>AUDIO_MIN_DURATION_SECONDS: shorter clips are rejected as empty.</summary>
    [Range(0.01, 5)] public double MinDurationSeconds { get; set; } = 0.1;

    /// <summary>AUDIO_DURATION_TOLERANCE_SECONDS: allowed difference between metadata and the WAV's real duration.</summary>
    [Range(0.01, 2)] public double DurationToleranceSeconds { get; set; } = 0.2;

    /// <summary>EVENT_CLOCK_SKEW_SECONDS: tolerance for client clocks when checking that an event lies inside its session.</summary>
    [Range(0, 3600)] public int ClockSkewSeconds { get; set; } = 300;

    /// <summary>AUDIO_RETENTION_HOURS: stored audio is deleted this long after receipt.</summary>
    [Range(0.01, 8760)] public double AudioRetentionHours { get; set; } = 24;

    /// <summary>EVENT_MAINTENANCE_ENABLED / _INTERVAL_SECONDS: background re-publish, cleanup and retention.</summary>
    public bool MaintenanceEnabled { get; set; } = true;
    [Range(1, 3600)] public int MaintenanceIntervalSeconds { get; set; } = 30;

    /// <summary>EVENT_REPUBLISH_AFTER_SECONDS: RECEIVED events older than this are re-published.</summary>
    [Range(1, 3600)] public int RepublishAfterSeconds { get; set; } = 15;

    /// <summary>EVENT_MISSING_AUDIO_AFTER_MINUTES: RECEIVED events whose audio never arrived become FAILED.</summary>
    [Range(1, 1440)] public int MissingAudioAfterMinutes { get; set; } = 10;

    /// <summary>EVENT_STAGING_MAX_AGE_MINUTES: staged files older than this are leftovers and are deleted.</summary>
    [Range(1, 1440)] public int StagingMaxAgeMinutes { get; set; } = 60;

    [Range(1, 600)] public int PublishLeaseSeconds { get; set; } = 30;

    /// <summary>
    /// A RECEIVED event whose file is missing is only treated as an interrupted upload (and repaired by a retried
    /// upload) after this long; younger ones are still being written by a concurrent request.
    /// </summary>
    [Range(1, 3600)] public int RepairAfterSeconds { get; set; } = 30;
    [Range(1024, 1_048_576)] public int MaxMetadataBytes { get; set; } = 16 * 1024;
    [Range(1, 10_000)] public int ListLimit { get; set; } = 500;

    public TimeSpan ClockSkew => TimeSpan.FromSeconds(ClockSkewSeconds);
    public TimeSpan AudioRetention => TimeSpan.FromHours(AudioRetentionHours);
}
