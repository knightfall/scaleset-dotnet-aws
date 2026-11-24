.PHONY: check check-dotnet check-cdk

check: check-dotnet check-cdk

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
