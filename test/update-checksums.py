"""Regenerate checksums from staged Git bytes. Developer tool; no network access."""
import hashlib
import pathlib
import subprocess


def main() -> None:
    root = pathlib.Path(__file__).resolve().parent.parent
    tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode("utf-8").split("\0")
    lines = []
    for path in sorted(p for p in tracked if p and p != "SHA256SUMS.txt"):
        if "\n" in path or "\r" in path:
            raise ValueError("A checksum path cannot contain a newline")
        process = subprocess.Popen(["git", "cat-file", "blob", ":" + path], cwd=root, stdout=subprocess.PIPE)
        digest = hashlib.sha256()
        with process.stdout:
            for chunk in iter(lambda: process.stdout.read(1024 * 1024), b""):
                digest.update(chunk)
        if process.wait() != 0:
            raise RuntimeError("Could not hash staged file: " + path)
        lines.append(digest.hexdigest() + "  " + path)
    (root / "SHA256SUMS.txt").write_bytes(("\n".join(lines) + "\n").encode("utf-8"))
    print("CHECKSUM_MANIFEST_WRITTEN", len(lines))


if __name__ == "__main__":
    main()
