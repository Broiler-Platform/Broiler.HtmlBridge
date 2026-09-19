"""Exercise the CI guard against isolated source trees; no engine restore is needed."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


GUARD = Path(__file__).with_name("check-engine-neutrality.sh")
CONTRACTS = "Broiler.HtmlBridge.Jseal"


class EngineNeutralityTests(unittest.TestCase):
    def check_tree(self, references, budget=0, project="Binding"):
        with tempfile.TemporaryDirectory(prefix="broiler-neutrality-") as directory:
            root = Path(directory)
            (root / "scripts").mkdir()
            (root / "eng").mkdir()
            shutil.copyfile(GUARD, root / "scripts/check-engine-neutrality.sh")
            projects = {}
            for name in {CONTRACTS, project}:
                folder = root / "src" / name
                folder.mkdir(parents=True)
                items = references if name == project else ""
                (folder / f"{name}.csproj").write_text(
                    f"<Project><ItemGroup>{items}</ItemGroup></Project>", encoding="utf-8"
                )
                (folder / "Example.cs").write_text("class Example {}\n", encoding="utf-8")
                projects[name] = dict(
                    engineReferences=0,
                    engineProjectRefs=budget if name == project else 0,
                    guestEvalSites=0,
                )
            (root / "eng/jseal-budget.json").write_text(
                json.dumps({"projects": projects}), encoding="utf-8"
            )
            return subprocess.run(
                [shutil.which("bash") or "bash", "scripts/check-engine-neutrality.sh"],
                cwd=root, capture_output=True, text=True, timeout=30,
            )

    def assert_rejected(self, result, reason):
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(reason, result.stdout + result.stderr)

    def test_engine_packages_cannot_escape_a_zero_budget(self):
        for package in ["Broiler.JavaScript.Engine", "Broiler.VM.Runtime", "broiler.vm.binary"]:
            with self.subTest(package=package):
                result = self.check_tree(f'<PackageReference Include="{package}" />')
                self.assert_rejected(result, "over budget")

    def test_existing_engine_package_fits_its_budget(self):
        result = self.check_tree('<PackageReference Include="Broiler.VM.Runtime" />', budget=1)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_project_and_package_dependencies_share_the_budget(self):
        result = self.check_tree(
            '<ProjectReference Include="$(BroilerVmRoot)/src/Broiler.VM.Runtime/Runtime.csproj" />'
            '<PackageReference Include="Broiler.JavaScript.Engine" />', budget=1,
        )
        self.assert_rejected(result, "over budget")

    def test_legacy_project_paths_still_count(self):
        for path in ["../../Broiler.JS/Engine.csproj", "../../Broiler.VM/Runtime.csproj",
                     "$(BroilerJsRoot)/Engine.csproj", "$(BroilerVmRoot)/Runtime.csproj"]:
            with self.subTest(path=path):
                result = self.check_tree(f'<ProjectReference Include="{path}" />')
                self.assert_rejected(result, "over budget")

    def test_other_packages_and_commented_references_do_not_count(self):
        result = self.check_tree(
            '<PackageReference Include="Broiler.Dom.Html" />'
            '<PackageReference Include="Broiler.VMTools" />'
            '<!-- <PackageReference Include="Broiler.VM.Runtime" /> -->'
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_contracts_cannot_reference_even_an_unrelated_package(self):
        result = self.check_tree('<PackageReference Include="Example" />', project=CONTRACTS)
        self.assert_rejected(result, "declares a PackageReference")

    def test_malformed_project_fails_closed(self):
        result = self.check_tree('<PackageReference Include="Broiler.VM.Runtime"')
        self.assert_rejected(result, "could not be parsed as XML")


if __name__ == "__main__":
    unittest.main()
