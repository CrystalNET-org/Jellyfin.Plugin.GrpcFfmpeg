#!/usr/bin/env python3
import json
import argparse

def update_manifest(version, url, checksum):
    with open('manifest.json', 'r') as f:
        manifest = json.load(f)

    manifest[0]['version'] = version
    manifest[0]['url'] = url
    manifest[0]['checksum'] = checksum

    with open('manifest.json', 'w') as f:
        json.dump(manifest, f, indent=2)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description='Update manifest.json')
    parser.add_argument('--version', required=True)
    parser.add_argument('--url', required=True)
    parser.add_argument('--checksum', required=True)
    args = parser.parse_args()

    update_manifest(args.version, args.url, args.checksum)
