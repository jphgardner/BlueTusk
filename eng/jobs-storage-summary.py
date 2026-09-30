"""Summarize retained raw storage observations without declaring production qualification."""

import argparse
import json
import math
from pathlib import Path


def distribution(values):
    ordered = sorted(values)
    if not ordered:
        return None
    return {
        "minimum": ordered[0],
        "p50": ordered[math.ceil(len(ordered) * 0.50) - 1],
        "p95": ordered[math.ceil(len(ordered) * 0.95) - 1],
        "p99": ordered[math.ceil(len(ordered) * 0.99) - 1],
        "maximum": ordered[-1],
    }


def fit(samples, values):
    times = [row["ElapsedSeconds"] for row in samples]
    if len(times) < 2:
        return None
    mean_time = sum(times) / len(times)
    mean_value = sum(values) / len(values)
    denominator = sum((time - mean_time) ** 2 for time in times)
    slope = sum((time - mean_time) * (value - mean_value) for time, value in zip(times, values)) / denominator
    return {
        "samples": len(times), "firstSeconds": times[0], "lastSeconds": times[-1],
        "firstBytes": values[0], "lastBytes": values[-1], "minimumBytes": min(values),
        "maximumBytes": max(values), "leastSquaresMiBPerMinute": slope * 60 / 1048576,
        "netGrowthBytes": values[-1] - values[0],
    }


def product_summary(result):
    duration = result["OfferedDurationSeconds"]
    samples = [row for row in result["StorageSamples"]
               if row["Physical"] is not None and row["ElapsedSeconds"] < duration and row["RetainedPrimaryRows"] > 0]
    if len(samples) < 2:
        raise ValueError("Physical storage campaign has too few sustained samples")
    first = samples[0]["Physical"]
    last = samples[-1]["Physical"]
    automatic = result["AutomaticVacuumObservationSeconds"]
    late = [row for row in samples if row["ElapsedSeconds"] >= max(automatic * 2, duration / 2)]
    windows = {
        "automaticOnly": [row for row in samples if row["ElapsedSeconds"] < automatic],
        "maintenanceSettling": [row for row in samples if automatic <= row["ElapsedSeconds"] < max(automatic * 2, duration / 2)],
        "lateMaintenance": late,
    }
    relations = []
    for relation in last["Relations"]:
        name = (relation["Schema"], relation["Table"])
        observations = [next(item for item in row["Physical"]["Relations"]
                             if (item["Schema"], item["Table"]) == name) for row in late]
        initial = next(item for item in first["Relations"] if (item["Schema"], item["Table"]) == name)
        relations.append({
            "schema": name[0], "table": name[1],
            "lateTotal": fit(late, [item["TotalBytes"] for item in observations]),
            "lateHeap": fit(late, [item["HeapBytes"] for item in observations]),
            "lateIndexes": fit(late, [item["IndexBytes"] for item in observations]),
            "manualVacuumsObserved": relation["VacuumCount"] - initial["VacuumCount"],
            "autovacuumsObserved": relation["AutovacuumCount"] - initial["AutovacuumCount"],
            "peakEstimatedDeadRows": max(next(item["EstimatedDeadRows"] for item in row["Physical"]["Relations"]
                                              if (item["Schema"], item["Table"]) == name) for row in samples),
        })
    sample_seconds = samples[-1]["ElapsedSeconds"] - samples[0]["ElapsedSeconds"]
    return {
        "product": result["Product"], "offeredSeconds": duration, "verified": result["Verified"],
        "accepted": result["Accepted"], "rejected": result["Rejected"], "effects": result["DurableEffects"],
        "completionsPerSecondIncludingDrain": result["CompletionsPerSecond"],
        "maximumOutstanding": result["MaximumOutstanding"], "maximumRetainedPrimaryRows": max(row["RetainedPrimaryRows"] for row in samples),
        "maximumRetainedDispatchJobs": max(row["RetainedJobs"] for row in samples),
        "runtimePhysicalWindows": {name: fit(rows, [row["RuntimeRelationBytes"] for row in rows]) for name, rows in windows.items()},
        "relationReuse": relations,
        "vacuumDurationMilliseconds": distribution([row["DurationMilliseconds"] for row in result["Vacuums"]]),
        "manualVacuumStatements": len(result["Vacuums"]),
        "observedWalInsertBytes": last["WalPosition"] - first["WalPosition"],
        "observedWalMiBPerMinute": (last["WalPosition"] - first["WalPosition"]) / sample_seconds * 60 / 1048576,
        "statisticsWalBytesDelta": last["WalBytes"] - first["WalBytes"],
        "statisticsWalFullPageImageDelta": last["WalFullPageImages"] - first["WalFullPageImages"],
        "timedCheckpointDelta": last["TimedCheckpoints"] - first["TimedCheckpoints"],
        "requestedCheckpointDelta": last["RequestedCheckpoints"] - first["RequestedCheckpoints"],
        "checkpointWriteMillisecondsDelta": last["CheckpointWriteMilliseconds"] - first["CheckpointWriteMilliseconds"],
        "checkpointSyncMillisecondsDelta": last["CheckpointSyncMilliseconds"] - first["CheckpointSyncMilliseconds"],
        "processCpuMillisecondsAtLastSample": last["ProcessCpuMilliseconds"],
        "processAverageSingleCorePercentage": last["ProcessCpuMilliseconds"] / samples[-1]["ElapsedSeconds"] / 10,
        "runtime": result["Runtime"], "tenants": result["Tenants"],
        "finalRetainedPrimaryRows": result["StorageSamples"][-1]["RetainedPrimaryRows"],
        "finalRetainedDispatchJobs": result["StorageSamples"][-1]["RetainedJobs"],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8-sig"))
    fixture_path = Path(str(args.report) + ".fixture.jsonl")
    fixture_rows = [json.loads(line) for line in fixture_path.read_text(encoding="utf-8-sig").splitlines() if line]
    summary = {
        "report": str(args.report), "startedAtUtc": report["StartedAt"], "sourceSha256": report["SourceSha256"],
        "postgreSqlVersion": report["PostgreSqlVersion"],
        "measurementBoundary": "Dedicated fixture, shared development host; finite post-warmup campaign. Relation observations exclude measurement/business-effect tables. WAL/checkpoints are cluster scoped. Row estimates/statistics can lag. Late slope measures the final half after at least two initial observation periods. No indefinite plateau, hard disk bound, cross-format upgrade or production qualification is asserted.",
        "fixtureSampleCount": len(fixture_rows),
        "fixtureCpuPercentage": distribution([float(row["CpuPercentage"].rstrip("%")) for row in fixture_rows]),
        "fixtureBlockIoReported": sorted(set(row["BlockIo"] for row in fixture_rows)),
        "products": [product_summary(result) for result in report["Overload"]],
        "faults": report["Faults"],
    }
    output = args.output or Path(str(args.report) + ".summary.json")
    output.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print("Summary: " + str(output))


if __name__ == "__main__":
    main()
