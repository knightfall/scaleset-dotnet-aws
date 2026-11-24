.PHONY: check check-dotnet check-cdk check-lifecycle

MINISTACK_PYTHON ?= python3
LIFECYCLE_TESTS = tests/Knightfall.ScaleSet.Aws.Lifecycle.Tests/Knightfall.ScaleSet.Aws.Lifecycle.Tests.csproj

check: check-dotnet check-cdk
	$(MAKE) check-lifecycle

check-dotnet:
	dotnet restore Knightfall.ScaleSet.Aws.slnx --locked-mode
	dotnet build Knightfall.ScaleSet.Aws.slnx --configuration Release --no-restore
	dotnet test --solution Knightfall.ScaleSet.Aws.slnx --configuration Release --no-build

check-cdk:
	npm --prefix cdk ci
	npm --prefix cdk run lint
	npm --prefix cdk run format:check
	npm --prefix cdk run typecheck
	npm --prefix cdk test
	npm --prefix cdk run synth

check-lifecycle:
	dotnet restore $(LIFECYCLE_TESTS) --locked-mode
	dotnet build $(LIFECYCLE_TESTS) --configuration Release --no-restore
	$(MINISTACK_PYTHON) tests/ministack/run.py dotnet test --project $(LIFECYCLE_TESTS) --configuration Release --no-build
