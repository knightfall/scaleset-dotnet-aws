.PHONY: check check-dotnet check-cdk check-lifecycle check-integration restore-cdk

MINISTACK_PYTHON ?= python3
MINISTACK_TEST_OPTIONS ?=
LIFECYCLE_TESTS = tests/Knightfall.ScaleSet.Aws.Lifecycle.Tests/Knightfall.ScaleSet.Aws.Lifecycle.Tests.csproj
INTEGRATION_TESTS = tests/Knightfall.ScaleSet.Aws.IntegrationTests/Knightfall.ScaleSet.Aws.IntegrationTests.csproj

check: check-dotnet check-cdk
	$(MAKE) check-lifecycle
	$(MAKE) check-integration

check-dotnet:
	dotnet restore Knightfall.ScaleSet.Aws.slnx --locked-mode
	dotnet build Knightfall.ScaleSet.Aws.slnx --configuration Release --no-restore
	dotnet test --solution Knightfall.ScaleSet.Aws.slnx --configuration Release --no-build

restore-cdk:
	npm --prefix cdk ci --engine-strict --ignore-scripts

check-cdk: restore-cdk
	npm --prefix cdk run lint
	npm --prefix cdk run format:check
	npm --prefix cdk run typecheck
	npm --prefix cdk test
	npm --prefix cdk run synth

check-lifecycle:
	dotnet restore $(LIFECYCLE_TESTS) --locked-mode
	dotnet build $(LIFECYCLE_TESTS) --configuration Release --no-restore
	$(MINISTACK_PYTHON) tests/ministack/run.py dotnet test --project $(LIFECYCLE_TESTS) --configuration Release --no-build $(MINISTACK_TEST_OPTIONS)

check-integration:
	dotnet restore $(INTEGRATION_TESTS) --locked-mode
	dotnet build $(INTEGRATION_TESTS) --configuration Release --no-restore
	$(MINISTACK_PYTHON) tests/ministack/run.py --with-iam dotnet test --project $(INTEGRATION_TESTS) --configuration Release --no-build $(MINISTACK_TEST_OPTIONS)
