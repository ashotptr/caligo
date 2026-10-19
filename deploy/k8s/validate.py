#!/usr/bin/env python3
import subprocess
import sys
from pathlib import Path

try:
    import yaml
except ImportError:
    sys.exit("PyYAML is required: pip install pyyaml")

ROOT = Path(__file__).resolve().parent
OVERLAYS = ["dev", "prod"]

EXTERNALLY_SUPPLIED: dict[str, set[str]] = {
    "prod": {"caligo-secrets"},
}

def build(overlay: str) -> list[dict]:
    result = subprocess.run(
        ["kubectl", "kustomize", str(ROOT / "overlays" / overlay)],
        capture_output=True, text=True,
    )

    if result.returncode != 0:
        sys.exit(f"kustomize build failed for {overlay}:\n{result.stderr}")

    return [doc for doc in yaml.safe_load_all(result.stdout) if doc]

def check(overlay: str, docs: list[dict]) -> list[str]:
    problems: list[str] = []

    by_kind: dict[str, dict[str, dict]] = {}
    for doc in docs:
        by_kind.setdefault(doc["kind"], {})[doc["metadata"]["name"]] = doc

    workloads = {**by_kind.get("Deployment", {}), **by_kind.get("StatefulSet", {})}

    secrets = set(by_kind.get("Secret", {})) | EXTERNALLY_SUPPLIED.get(overlay, set())
    configmaps = set(by_kind.get("ConfigMap", {}))
    services = by_kind.get("Service", {})

    for name, workload in workloads.items():
        pod = workload["spec"]["template"]
        pod_labels = pod["metadata"].get("labels", {})

        selector = workload["spec"]["selector"]["matchLabels"]
        if not selector.items() <= pod_labels.items():
            problems.append(f"{name}: selector {selector} does not match pod labels {pod_labels}")

        for container in pod["spec"]["containers"]:
            declared = {p["name"] for p in container.get("ports", []) if "name" in p}

            for probe_name in ("livenessProbe", "readinessProbe", "startupProbe"):
                probe = container.get(probe_name)
                if not probe or "httpGet" not in probe:
                    continue

                port = probe["httpGet"]["port"]
                if isinstance(port, str) and port not in declared:
                    problems.append(
                        f"{name}/{container['name']}: {probe_name} targets port '{port}', "
                        f"which the container does not declare (has: {sorted(declared) or 'none'})"
                    )

            for source in container.get("envFrom", []):
                if "secretRef" in source and source["secretRef"]["name"] not in secrets:
                    problems.append(
                        f"{name}/{container['name']}: envFrom Secret "
                        f"'{source['secretRef']['name']}' is not defined in the {overlay} overlay"
                    )
                if "configMapRef" in source and source["configMapRef"]["name"] not in configmaps:
                    problems.append(
                        f"{name}/{container['name']}: envFrom ConfigMap "
                        f"'{source['configMapRef']['name']}' is not defined in the {overlay} overlay"
                    )

            security = container.get("securityContext", {})
            if security.get("readOnlyRootFilesystem"):
                mounts = {m["mountPath"] for m in container.get("volumeMounts", [])}
                if "/tmp" not in mounts:
                    problems.append(
                        f"{name}/{container['name']}: readOnlyRootFilesystem without a /tmp volume"
                    )

            if "resources" not in container or "requests" not in container.get("resources", {}):
                problems.append(f"{name}/{container['name']}: no resource requests")

    for name, service in services.items():
        selector = service["spec"].get("selector")
        if not selector:
            continue

        if not any(
            selector.items() <= w["spec"]["template"]["metadata"].get("labels", {}).items()
            for w in workloads.values()
        ):
            problems.append(f"Service/{name}: selector {selector} matches no workload")

    for name, ingress in by_kind.get("Ingress", {}).items():
        for rule in ingress["spec"].get("rules", []):
            for path in rule.get("http", {}).get("paths", []):
                backend = path["backend"]["service"]
                if backend["name"] not in services:
                    problems.append(f"Ingress/{name}: backend Service '{backend['name']}' does not exist")
                else:
                    port = backend.get("port", {})
                    if "name" in port:
                        names = {p.get("name") for p in services[backend["name"]]["spec"]["ports"]}
                        if port["name"] not in names:
                            problems.append(
                                f"Ingress/{name}: Service '{backend['name']}' has no port "
                                f"named '{port['name']}'"
                            )

    for kind, path in (("HorizontalPodAutoscaler", ("spec", "scaleTargetRef", "name")),
                       ("ScaledObject", ("spec", "scaleTargetRef", "name"))):
        for name, doc in by_kind.get(kind, {}).items():
            target = doc
            for key in path:
                target = target.get(key, {}) if isinstance(target, dict) else {}
            if target and target not in workloads:
                problems.append(f"{kind}/{name}: targets '{target}', which is not in this overlay")

    for name, doc in by_kind.get("ScaledObject", {}).items():
        target = doc["spec"]["scaleTargetRef"]["name"]
        if target in workloads and "replicas" in workloads[target]["spec"]:
            problems.append(
                f"ScaledObject/{name}: Deployment '{target}' still sets spec.replicas; "
                f"KEDA and the Deployment will fight over it"
            )

    return problems

def main() -> int:
    failed = False

    for overlay in OVERLAYS:
        docs = build(overlay)
        problems = check(overlay, docs)

        kinds = {}
        for doc in docs:
            kinds[doc["kind"]] = kinds.get(doc["kind"], 0) + 1

        summary = ", ".join(f"{k}x{v}" for k, v in sorted(kinds.items()))
        print(f"{overlay}: {len(docs)} objects ({summary})")

        external = EXTERNALLY_SUPPLIED.get(overlay)
        if external:
            print(f"  (expects {', '.join(sorted(external))} to be created out of band)")

        if problems:
            failed = True
            for problem in problems:
                print(f"  FAIL  {problem}")
        else:
            print("  all cross-resource checks passed")

    return 1 if failed else 0

if __name__ == "__main__":
    sys.exit(main())
