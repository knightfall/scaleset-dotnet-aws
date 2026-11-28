import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { Template } from "aws-cdk-lib/assertions";
import { compileConfig } from "../../lib/config/compiler.ts";
import { synthesizeController } from "../../lib/infrastructure/controller-stack.ts";
import { resolveControllerTelemetry } from "../../lib/infrastructure/controller-telemetry.ts";
import type { ControllerTelemetrySettings } from "../../lib/infrastructure/controller-telemetry.ts";
import { deployController } from "../../lib/infrastructure/deploy-controller.ts";
import { controllerStackName } from "../../lib/infrastructure/deployment-input.ts";

const configPath = fileURLToPath(
  new URL("../../../testdata/config/ec2-only-pinned.yaml", import.meta.url),
);
const compiled = compileConfig(readFileSync(configPath, "utf8"));
const { aws } = compiled.runtime.spec;
const secretPrefix = `arn:aws:secretsmanager:${aws.region}:${aws.account}:secret:`;
const commonSecret = `${secretPrefix}controller-otlp-common-X1y2z3`;
const tracesSecret = `${secretPrefix}controller-otlp-traces-Y2z3x1`;
const metricsSecret = `${secretPrefix}controller-otlp-metrics-Z3x1y2`;
const logsSecret = `${secretPrefix}controller-otlp-logs-a4B5c6`;
const key = `arn:aws:kms:${aws.region}:${aws.account}:key/00000000-0000-0000-0000-000000000001`;

const telemetry = {
  sdkDisabled: false,
  serviceName: "controller-telemetry-fixture",
  deploymentEnvironment: "fixture",
  metricExportIntervalMs: 1000,
  sampler: { type: "parentbased_traceidratio", ratio: 0.25 },
  exporters: {
    common: {
      endpoint: "https://collector.example.invalid/base/",
      protocol: "grpc",
      timeoutMs: 5000,
      headersSecret: { secretArn: commonSecret, kmsKeyArn: key },
    },
    traces: {
      endpoint: "http://127.0.0.1:4318/v1/traces",
      protocol: "http/protobuf",
      timeoutMs: 1,
      headersSecret: { secretArn: tracesSecret },
    },
    metrics: {
      endpoint: "https://metrics.example.invalid/custom",
      timeoutMs: 2500,
      headersSecret: { secretArn: metricsSecret, kmsKeyArn: key },
    },
    logs: {
      protocol: "http/protobuf",
      headersSecret: { secretArn: logsSecret },
    },
  },
} satisfies ControllerTelemetrySettings;

interface Statement {
  readonly Action: string | string[];
  readonly Effect: string;
  readonly Resource: unknown;
  readonly Condition?: unknown;
}

function statements(template: Template, role: string) {
  const roleId = Object.keys(template.findResources("AWS::IAM::Role")).find(
    (logicalId) => logicalId.startsWith(role),
  );
  assert.ok(roleId);
  return Object.values(template.findResources("AWS::IAM::Policy"))
    .filter(({ Properties }) =>
      (Properties.Roles as { Ref?: string }[]).some(
        ({ Ref }) => Ref === roleId,
      ),
    )
    .flatMap(
      ({ Properties }) => Properties.PolicyDocument.Statement as Statement[],
    );
}

function taskContainer(template: Template) {
  const [task, ...others] = Object.values(
    template.findResources("AWS::ECS::TaskDefinition"),
  );
  assert.ok(task);
  assert.deepEqual(others, []);
  const [container, ...sidecars] = task.Properties
    .ContainerDefinitions as Record<string, unknown>[];
  assert.ok(container);
  assert.deepEqual(sidecars, []);
  return container;
}

function checkRuntimeAsset(
  directory: string,
  expected: ReturnType<typeof compileConfig>,
) {
  const asset = readdirSync(directory).find((name) =>
    /^asset\..+\.json$/u.test(name),
  );
  assert.ok(asset);
  assert.equal(readFileSync(join(directory, asset), "utf8"), expected.json);
}

for (const mode of ["active", "dry-run"] as const) {
  test(`${mode} controller injects only typed telemetry settings and exact execution-role secret grants`, () => {
    const configuration = compileConfig(
      JSON.stringify({
        ...compiled.runtime,
        kind: "RunnerController",
        spec: { ...compiled.runtime.spec, mode },
      }),
    );
    const directory = mkdtempSync(
      join(tmpdir(), "scaleset-telemetry-template-"),
    );
    try {
      const baseline = Template.fromJSON(
        synthesizeController(configuration, "1", join(directory, "base"))
          .template,
      );
      const output = join(directory, "observed");
      const template = Template.fromJSON(
        synthesizeController(configuration, "1", output, { telemetry })
          .template,
      );
      const container = taskContainer(template);
      assert.equal(container.Name, "Controller");
      assert.deepEqual(
        container.Environment,
        Object.entries({
          OTEL_SDK_DISABLED: "false",
          OTEL_SERVICE_NAME: "controller-telemetry-fixture",
          OTEL_RESOURCE_ATTRIBUTES: "deployment.environment.name=fixture",
          OTEL_METRIC_EXPORT_INTERVAL: "1000",
          OTEL_TRACES_SAMPLER: "parentbased_traceidratio",
          OTEL_TRACES_SAMPLER_ARG: "0.25",
          OTEL_EXPORTER_OTLP_ENDPOINT:
            "https://collector.example.invalid/base/",
          OTEL_EXPORTER_OTLP_PROTOCOL: "grpc",
          OTEL_EXPORTER_OTLP_TIMEOUT: "5000",
          OTEL_EXPORTER_OTLP_TRACES_ENDPOINT: "http://127.0.0.1:4318/v1/traces",
          OTEL_EXPORTER_OTLP_TRACES_PROTOCOL: "http/protobuf",
          OTEL_EXPORTER_OTLP_TRACES_TIMEOUT: "1",
          OTEL_EXPORTER_OTLP_METRICS_ENDPOINT:
            "https://metrics.example.invalid/custom",
          OTEL_EXPORTER_OTLP_METRICS_TIMEOUT: "2500",
          OTEL_EXPORTER_OTLP_LOGS_PROTOCOL: "http/protobuf",
          SCALESET_RUNTIME_BUCKET_OWNER: aws.account,
        }).map(([Name, Value]) => ({ Name, Value })),
      );
      assert.deepEqual(container.Secrets, [
        {
          Name: "OTEL_EXPORTER_OTLP_HEADERS",
          ValueFrom: `${commonSecret}::AWSCURRENT:`,
        },
        {
          Name: "OTEL_EXPORTER_OTLP_TRACES_HEADERS",
          ValueFrom: `${tracesSecret}::AWSCURRENT:`,
        },
        {
          Name: "OTEL_EXPORTER_OTLP_METRICS_HEADERS",
          ValueFrom: `${metricsSecret}::AWSCURRENT:`,
        },
        {
          Name: "OTEL_EXPORTER_OTLP_LOGS_HEADERS",
          ValueFrom: `${logsSecret}::AWSCURRENT:`,
        },
      ]);
      const execution = statements(template, "ControllerExecutionRole");
      const extra = execution.filter(({ Action }) =>
        [Action]
          .flat()
          .some(
            (action) =>
              action.startsWith("secretsmanager:") || action.startsWith("kms:"),
          ),
      );
      assert.deepEqual(extra, [
        {
          Action: "secretsmanager:GetSecretValue",
          Effect: "Allow",
          Resource: [commonSecret, logsSecret, metricsSecret, tracesSecret],
          Condition: {
            StringEquals: { "secretsmanager:VersionStage": "AWSCURRENT" },
          },
        },
        ...[commonSecret, metricsSecret].map((secretArn) => ({
          Action: "kms:Decrypt",
          Effect: "Allow",
          Resource: key,
          Condition: {
            StringEquals: {
              "kms:ViaService": {
                "Fn::Join": [
                  "",
                  [`secretsmanager.${aws.region}.`, { Ref: "AWS::URLSuffix" }],
                ],
              },
              "kms:EncryptionContext:SecretARN": secretArn,
            },
          },
        })),
      ]);
      assert.deepEqual(
        execution.filter(
          (statement) => !extra.some((entry) => entry === statement),
        ),
        statements(baseline, "ControllerExecutionRole"),
      );
      const taskRole = statements(template, "ControllerTaskRole");
      assert.deepEqual(taskRole, statements(baseline, "ControllerTaskRole"));
      if (mode === "dry-run")
        assert.ok(
          taskRole.every(
            ({ Action }) =>
              ![Action].flat().includes("secretsmanager:GetSecretValue"),
          ),
        );
      assert.equal(taskContainer(baseline).Secrets, undefined);
      assert.deepEqual(taskContainer(baseline).Environment, [
        { Name: "SCALESET_RUNTIME_BUCKET_OWNER", Value: aws.account },
      ]);
      assert.deepEqual(template.toJSON().Outputs, baseline.toJSON().Outputs);
      const unaffectedResources = (source: Template) =>
        Object.fromEntries(
          Object.entries(source.toJSON().Resources)
            .filter(
              ([id]) =>
                !id.startsWith("ControllerTask") ||
                id.startsWith("ControllerTaskRole"),
            )
            .filter(
              ([id]) => !id.startsWith("ControllerExecutionRoleDefaultPolicy"),
            ),
        );
      assert.deepEqual(
        unaffectedResources(template),
        unaffectedResources(baseline),
      );
      checkRuntimeAsset(output, configuration);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });
}

test("typed telemetry rejects unknown fields, plaintext headers and malformed host settings", () => {
  const invalidCases = [
    [{ OTEL_RESOURCE_ATTRIBUTES: "secret=synthetic-plaintext" }, "telemetry"],
    [{ sdkDisabled: "true" }, "sdkDisabled"],
    [{ deploymentEnvironment: "invalid environment" }, "deploymentEnvironment"],
    [{ metricExportIntervalMs: 999 }, "metricExportIntervalMs"],
    [{ metricExportIntervalMs: 3600001 }, "metricExportIntervalMs"],
    [{ sampler: { type: "parentbased_always_on", ratio: 1 } }, "sampler.ratio"],
    [
      { sampler: { type: "parentbased_traceidratio", ratio: -0.1 } },
      "sampler.ratio",
    ],
    [
      { exporters: { common: { timeoutMs: 5001 } } },
      "exporters.common.timeoutMs",
    ],
    [
      {
        exporters: {
          logs: { endpoint: "https://user:synthetic-plaintext@invalid" },
        },
      },
      "exporters.logs.endpoint",
    ],
    [
      {
        exporters: {
          logs: {
            endpoint: "https://example.invalid/?token=synthetic-plaintext",
          },
        },
      },
      "exporters.logs.endpoint",
    ],
    [
      { exporters: { metrics: { protocol: "http/json" } } },
      "exporters.metrics.protocol",
    ],
    [
      {
        exporters: { common: { headers: "authorization=synthetic-plaintext" } },
      },
      "exporters.common",
    ],
    [
      {
        exporters: {
          traces: { headersSecret: "authorization=synthetic-plaintext" },
        },
      },
      "exporters.traces.headersSecret",
    ],
    [
      {
        exporters: {
          traces: { headersSecret: { secretArn: `${secretPrefix}partial` } },
        },
      },
      "exporters.traces.headersSecret.secretArn",
    ],
    [
      {
        exporters: {
          traces: {
            headersSecret: {
              secretArn: commonSecret.replace(aws.account, "999900001111"),
            },
          },
        },
      },
      "exporters.traces.headersSecret.secretArn",
    ],
    [
      {
        exporters: {
          common: {
            headersSecret: {
              secretArn: commonSecret,
              kmsKeyArn: key.replace("key/", "alias/"),
            },
          },
        },
      },
      "exporters.common.headersSecret.kmsKeyArn",
    ],
  ] as const;
  for (const [input, field] of invalidCases) {
    assert.throws(() => resolveControllerTelemetry(input, aws), {
      message: `Invalid controller telemetry setting ${field}.`,
    });
  }
  assert.deepEqual(
    resolveControllerTelemetry(
      { sdkDisabled: true, sampler: { type: "parentbased_always_on" } },
      aws,
    ).environment,
    { OTEL_SDK_DISABLED: "true", OTEL_TRACES_SAMPLER: "parentbased_always_on" },
  );
  assert.equal(
    resolveControllerTelemetry(
      { sampler: { type: "parentbased_traceidratio", ratio: 1e-9 } },
      aws,
    ).environment.OTEL_TRACES_SAMPLER_ARG,
    "0.000000001",
  );
});

test("CDK uses the same literal endpoint paths as the runtime", () => {
  const cases = JSON.parse(
    readFileSync(
      new URL(
        "../../../testdata/telemetry/otlp-endpoints.json",
        import.meta.url,
      ),
      "utf8",
    ),
  ) as { endpoint: string; path: string | null }[];
  for (const { endpoint, path } of cases) {
    const input = {
      exporters: {
        traces: {
          endpoint,
          protocol: "http/protobuf",
          headersSecret: { secretArn: commonSecret },
        },
      },
    };
    if (path === null) {
      assert.throws(() => resolveControllerTelemetry(input, aws), {
        message:
          "Invalid controller telemetry setting exporters.traces.endpoint.",
      });
      continue;
    }

    const resolved = resolveControllerTelemetry(input, aws);
    assert.equal(
      resolved.environment.OTEL_EXPORTER_OTLP_TRACES_ENDPOINT,
      endpoint,
    );
    assert.equal(new URL(endpoint).pathname, path);
    assert.equal(resolved.headers[0]?.secretArn, commonSecret);
  }
});

test("credentialed remote OTLP requires TLS for each resolved signal", () => {
  const secure = "https://collector.example.invalid";
  const unsecured = "http://collector.example.invalid";
  const secret = { secretArn: commonSecret };

  for (const signal of ["traces", "metrics", "logs"]) {
    for (const protocol of ["grpc", "http/protobuf"]) {
      for (const inherited of [false, true]) {
        const exporters = {
          common: {
            endpoint: secure,
            ...(inherited ? { headersSecret: secret } : {}),
          },
          [signal]: {
            endpoint: unsecured,
            protocol,
            ...(inherited ? {} : { headersSecret: secret }),
          },
        };
        assert.throws(() => resolveControllerTelemetry({ exporters }, aws), {
          message: `Invalid controller telemetry setting exporters.${signal}.endpoint.`,
        });
      }
    }
  }

  for (const endpoint of [
    secure,
    "http://localhost:4317",
    "http://127.0.0.2:4317",
    "http://[::1]:4317",
  ]) {
    const resolved = resolveControllerTelemetry(
      { exporters: { common: { endpoint, headersSecret: secret } } },
      aws,
    );
    assert.equal(resolved.environment.OTEL_EXPORTER_OTLP_ENDPOINT, endpoint);
    assert.deepEqual(resolved.headers, [
      { name: "OTEL_EXPORTER_OTLP_HEADERS", ...secret, kmsKeyArn: undefined },
    ]);
  }

  assert.throws(
    () =>
      resolveControllerTelemetry(
        {
          exporters: {
            common: { endpoint: unsecured },
            metrics: { headersSecret: secret },
          },
        },
        aws,
      ),
    {
      message:
        "Invalid controller telemetry setting exporters.common.endpoint.",
    },
  );
});

test("telemetry and explicit Fargate preflight coexist in the same controller container", () => {
  const configuration = compileConfig(
    readFileSync(
      new URL(
        "../../../testdata/config/mixed-ec2-fargate.yaml",
        import.meta.url,
      ),
      "utf8",
    ),
  );
  const directory = mkdtempSync(
    join(tmpdir(), "scaleset-telemetry-with-preflight-"),
  );
  const fargatePreflight = JSON.stringify({
    profiles: {
      "linux-shell": {
        image: `111122223333.dkr.ecr.ap-southeast-2.amazonaws.com/runner@sha256:${"a".repeat(64)}`,
        entryPoint: ["/opt/scaleset/RunnerBootstrap"],
        command: ["fargate"],
      },
    },
  });
  try {
    const template = Template.fromJSON(
      synthesizeController(configuration, "1", directory, {
        fargatePreflight,
        telemetry: { serviceName: "coexisting-controller" },
      }).template,
    );
    const container = taskContainer(template);
    assert.deepEqual(container.Environment, [
      { Name: "OTEL_SERVICE_NAME", Value: "coexisting-controller" },
      { Name: "SCALESET_RUNTIME_BUCKET_OWNER", Value: aws.account },
      { Name: "SCALESET_FARGATE_PREFLIGHT", Value: fargatePreflight },
    ]);
    assert.equal(container.Secrets, undefined);
    checkRuntimeAsset(directory, configuration);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("the synth CLI consumes an explicit telemetry document and ignores ambient OTLP values", () => {
  const directory = mkdtempSync(join(tmpdir(), "scaleset-telemetry-cli-"));
  const document = join(directory, "telemetry.json");
  const outdir = join(directory, "out");
  const args = [
    "--config",
    configPath,
    "--generation",
    "1",
    "--telemetry",
    document,
  ];
  const run = (entry: string) =>
    spawnSync(
      process.execPath,
      [
        fileURLToPath(new URL(`../../bin/${entry}.ts`, import.meta.url)),
        ...args,
      ],
      {
        encoding: "utf8",
        env: {
          CDK_OUTDIR: outdir,
          OTEL_EXPORTER_OTLP_HEADERS: "authorization=synthetic-plaintext",
        },
      },
    );
  try {
    writeFileSync(document, JSON.stringify(telemetry));
    const valid = run("controller");
    assert.equal(valid.status, 0, valid.stderr);
    const template = readFileSync(
      join(outdir, "Controller.template.json"),
      "utf8",
    );
    assert.ok(template.includes(commonSecret));
    assert.ok(!template.includes("synthetic-plaintext"));
    checkRuntimeAsset(outdir, compiled);
    writeFileSync(
      document,
      '{"serviceName":"fixture","serviceName":"synthetic-plaintext"}',
    );
    for (const entry of ["controller", "deploy-controller"]) {
      const invalid = run(entry);
      assert.equal(invalid.status, 1);
      assert.equal(
        invalid.stderr.trim(),
        "Invalid controller telemetry setting document.",
      );
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("the protected deploy prepares a candidate with the same validated telemetry and runtime bytes", (context) => {
  const invocations: string[] = [];
  const recovery = mkdtempSync(join(tmpdir(), "scaleset-telemetry-state-"));
  context.after(() => rmSync(recovery, { recursive: true, force: true }));
  assert.throws(
    () =>
      deployController(
        { compiled, generation: "1", telemetry },
        (program, args) => {
          if (program === process.execPath) {
            invocations.push("prepare");
            const assembly = args[args.indexOf("--app") + 1];
            const template = Template.fromString(
              readFileSync(join(assembly, "Controller.template.json"), "utf8"),
            );
            assert.deepEqual(
              (taskContainer(template).Secrets as { ValueFrom: string }[]).map(
                ({ ValueFrom }) => ValueFrom,
              ),
              [commonSecret, tracesSecret, metricsSecret, logsSecret].map(
                (arn) => `${arn}::AWSCURRENT:`,
              ),
            );
            checkRuntimeAsset(assembly, compiled);
            return {
              status: 1,
              stdout: "",
              stderr: "synthetic preparation failure",
            };
          }
          assert.equal(program, "aws");
          if (args.includes("get-caller-identity")) {
            invocations.push("identity");
            return {
              status: 0,
              stdout: JSON.stringify({ Account: aws.account }),
              stderr: "",
            };
          }
          if (args.includes("describe-stacks")) {
            invocations.push("stack");
            return {
              status: 254,
              stdout: "",
              stderr: `An error occurred (ValidationError) when calling the DescribeStacks operation: Stack with id ${controllerStackName(compiled.runtime)} does not exist`,
            };
          }
          assert.ok(args.includes("describe-table"));
          invocations.push("table");
          return {
            status: 254,
            stdout: "",
            stderr:
              "An error occurred (ResourceNotFoundException) when calling the DescribeTable operation: Not found",
          };
        },
        recovery,
      ),
    /CDK change set preparation failed/u,
  );
  assert.deepEqual(invocations, ["identity", "stack", "table", "prepare"]);
});
