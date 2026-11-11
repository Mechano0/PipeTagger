# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed
Only release builds of `PipeTagger.Loader.dll` and `PipeTagger.Core.dll` produced by the
GitHub Actions workflow in this repository (`.github/workflows/build.yml`) from source code in this
repository. Nothing built on a personal computer is signed.

## Team roles
| Role | Members |
|---|---|
| Committers and reviewers | [Mechano0](https://github.com/Mechano0) |
| Approvers | [Mechano0](https://github.com/Mechano0) |

## Privacy statement
This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it.
It reads and writes only the open Revit model and the user's Revit add-in folder.
