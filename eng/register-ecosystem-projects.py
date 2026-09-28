"""Register implemented expansion projects without modifying existing release policies."""
from pathlib import Path
import json
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
PRODUCTS = ("Events", "Jobs", "Documents", "Projections", "Search", "Schema", "Sql", "Studio", "Edge", "Workflows")
manifest_path = ROOT / "eng/product-families.json"
manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
solution_path = ROOT / "BlueTusk.slnx"
solution = ET.parse(solution_path).getroot()
folders = {folder.attrib["Name"]: folder for folder in solution.findall("Folder")}
registered = {node.attrib["Path"] for node in solution.iter("Project")}

for product in PRODUCTS:
    packages = []
    dependencies = set()
    for area, role in (("src", "Core"), ("tests", "Tests"), ("tooling", "Tools"), ("samples", "Samples")):
        for path in sorted((ROOT / area).glob(f"BlueTusk.{product}*/*.csproj")):
            if path.parent.name != f"BlueTusk.{product}" and not path.parent.name.startswith(f"BlueTusk.{product}."):
                continue
            document = ET.parse(path).getroot()
            family = document.findtext(".//BlueTuskProductFamily")
            if family != product:
                raise ValueError(f"{path}: expected declared family {product}, got {family}")
            relative = path.relative_to(ROOT).as_posix()
            if relative not in registered:
                folder_name = f"/{product}/{role}/"
                if folder_name not in folders:
                    folders[folder_name] = ET.SubElement(solution, "Folder", Name=folder_name)
                ET.SubElement(folders[folder_name], "Project", Path=relative)
                registered.add(relative)
            if area != "tests" and document.findtext(".//IsPackable") != "false":
                packages.append(relative)
            for reference in document.findall(".//ProjectReference"):
                reference_path = (path.parent / reference.attrib["Include"].replace("\\", "/")).resolve()
                reference_family = ET.parse(reference_path).getroot().findtext(".//BlueTuskProductFamily") or "Provider"
                if reference_family != product:
                    dependencies.add(reference_family)
    if packages:
        existing = manifest["families"].get(product)
        if existing is None:
            existing = {
                "versionFile": f"eng/versions/{product}.props",
                "publication": {
                    "enabled": False, "channel": "stable", "tagPrefix": product.lower(),
                    "requiredWorkflowEvidence": [
                        {"workflowFile": workflow, "allowedEvents": ["workflow_dispatch"]}
                        for workflow in ("build.yml", "security.yml", "performance.yml", "ecosystem-build.yml")
                    ],
                },
                "releaseDependencies": [], "packages": [],
            }
            manifest["families"][product] = existing
        if existing["publication"]["enabled"]:
            raise ValueError(f"Refusing to alter enabled publication policy for {product}")
        existing["packages"] = sorted(packages)
        existing["releaseDependencies"] = sorted(dependencies)
        if product == "Edge" and (ROOT / "clients/edge/package.json").is_file():
            existing["npmPackages"] = ["clients/edge"]

benchmark_projects = sorted(path for path in (ROOT / "benchmarks").glob("*/*.csproj")
                            if "Ecosystem" in path.parent.name or "LoadHarness" in path.parent.name)
for path in benchmark_projects:
    relative = path.relative_to(ROOT).as_posix()
    if relative not in registered:
        folder_name = "/Benchmarks/"
        if folder_name not in folders:
            folders[folder_name] = ET.SubElement(solution, "Folder", Name=folder_name)
        ET.SubElement(folders[folder_name], "Project", Path=relative)
        registered.add(relative)

solution[:] = sorted(solution, key=lambda node: node.attrib.get("Name", "").casefold())
for folder in solution.findall("Folder"):
    folder[:] = sorted(folder, key=lambda node: node.attrib["Path"].casefold())
ET.indent(solution, space="  ")
solution_path.write_text(ET.tostring(solution, encoding="unicode") + "\n", encoding="utf-8", newline="\n")
manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
print("Registered implemented ecosystem projects; publication remains disabled.")
