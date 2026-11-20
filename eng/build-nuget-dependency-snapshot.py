"""Build a GitHub dependency snapshot from the exact NuGet restore graphs."""

import argparse
import datetime
import json
from pathlib import Path
import subprocess
from urllib.parse import quote
import xml.etree.ElementTree as ET


SOLUTIONS = ("BlueTusk.slnx", "applications/BlueTusk.Applications.slnx")


def solution_projects(root, filename):
    solution = root / filename
    projects = []
    for member in ET.parse(solution).iter("Project"):
        project = (solution.parent / member.attrib["Path"]).resolve()
        project.relative_to(root)
        if project.suffix != ".csproj" or not project.is_file():
            raise ValueError(f"Missing C# project in {filename}: {member.attrib['Path']}")
        projects.append(project)
    if not projects or len(set(projects)) != len(projects):
        raise ValueError(f"Empty or duplicate project entries in {filename}")
    return projects


def project_manifest(root, project):
    relative = project.relative_to(root).as_posix()
    assets_file = project.parent / "obj" / "project.assets.json"
    assets = json.loads(assets_file.read_text(encoding="utf-8-sig"))
    restored_path = Path(assets["project"]["restore"]["projectPath"]).resolve()
    if restored_path != project or any(entry["level"] == "Error" for entry in assets.get("logs", [])):
        raise ValueError(f"Missing successful restore for {relative}")

    frameworks = assets["project"]["frameworks"]
    targets = assets["targets"]
    if not frameworks or not targets:
        raise ValueError(f"Missing restored framework graphs for {relative}")
    parts = relative.split("/")
    is_development = parts[0] in ("tests", "benchmarks") or parts[:2] == ["applications", "tests"]
    scope = "development" if is_development else "runtime"
    resolved = {}
    for target_name, target in targets.items():
        framework = target_name.partition("/")[0]
        direct = {name.casefold() for name in frameworks[framework].get("dependencies", {})}
        libraries_by_id = {}
        package_urls = {}
        for identity, library in target.items():
            name, separator, version = identity.partition("/")
            key = name.casefold()
            if not name or not separator or not version or key in libraries_by_id:
                raise ValueError(f"Invalid dependency identity {identity} in {relative} ({target_name})")
            libraries_by_id[key] = library
            if library["type"] == "package":
                package_urls[key] = f"pkg:nuget/{quote(name, safe='')}@{quote(version, safe='')}"

        for key, url in package_urls.items():
            record = resolved.setdefault(url, {
                "package_url": url,
                "relationship": "indirect",
                "scope": scope,
                "dependencies": set(),
            })
            if key in direct:
                record["relationship"] = "direct"
            for child in libraries_by_id[key].get("dependencies", {}):
                child_key = child.casefold()
                if child_key not in libraries_by_id:
                    raise ValueError(f"Missing dependency {child} for {url} in {relative} ({target_name})")
                if child_key in package_urls:
                    record["dependencies"].add(package_urls[child_key])

    for record in resolved.values():
        record["dependencies"] = sorted(record["dependencies"])
    return {"name": relative, "file": {"source_location": relative}, "resolved": resolved}


def write_snapshot(root, args):
    commit = subprocess.run(
        ["git", "-C", str(root), "rev-parse", "HEAD"],
        check=True, capture_output=True, text=True,
    ).stdout.strip()
    if commit != args.commit:
        raise ValueError("The restored checkout does not match the requested snapshot commit")

    core_projects, app_projects = (solution_projects(root, path) for path in SOLUTIONS)
    projects = sorted(set(core_projects + app_projects))
    manifests = {
        project.relative_to(root).as_posix(): project_manifest(root, project)
        for project in projects
    }
    dependencies = sum(len(manifest["resolved"]) for manifest in manifests.values())
    if len(projects) != len(core_projects) + len(app_projects) or dependencies == 0:
        raise ValueError("The solution inventory overlaps or the NuGet snapshot is empty")

    snapshot = {
        "version": 0,
        "sha": commit,
        "ref": args.ref,
        "job": {
            "correlator": "bluetusk-nuget",
            "id": args.run,
            "html_url": f"{args.repository_url}/actions/runs/{args.run}",
        },
        "detector": {
            "name": "BlueTusk NuGet restore",
            "version": "1.0.0",
            "url": f"{args.repository_url}/blob/{commit}/eng/build-nuget-dependency-snapshot.py",
        },
        "scanned": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "manifests": manifests,
    }
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(snapshot, output, sort_keys=True, separators=(",", ":"))
        output.write("\n")
    print(f"Verified {len(manifests)} project graphs ({len(app_projects)} application projects) and {dependencies} resolved package entries.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--ref", required=True)
    parser.add_argument("--run", required=True)
    parser.add_argument("--repository-url", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        write_snapshot(Path(__file__).resolve().parent.parent, args)
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"NuGet dependency snapshot: {error}\n")


if __name__ == "__main__":
    main()
