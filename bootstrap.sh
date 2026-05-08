#!/bin/bash

PULL_ARTIFACTS=""

while [[ $# -gt 0 ]]; do
	case "$1" in
		--pull-artifacts)
			PULL_ARTIFACTS="--pull-artifacts"
			shift
			;;
		*)
			echo "Unknown option: $1"
			exit 1
			;;
	esac
done

dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build --config Developer $PULL_ARTIFACTS
dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build-shaders
dotnet run --project ./engine/Tools/SboxBuild/SboxBuild.csproj -- build-content
