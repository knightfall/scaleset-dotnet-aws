import { readFileSync } from "node:fs";
import { CfnOutput, Duration, RemovalPolicy, Stack } from "aws-cdk-lib";
import type { ITable } from "aws-cdk-lib/aws-dynamodb";
import * as iam from "aws-cdk-lib/aws-iam";
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as logs from "aws-cdk-lib/aws-logs";
import type { Construct } from "constructs";
import { trackingTableDecrypt } from "./imported-encryption.ts";

export function helloIngress(
  scope: Construct,
  id: string,
  table: ITable,
  bootstrapRole: iam.IRole,
  reservedConcurrency: number,
  tableKeyArn?: string,
) {
  const stack = Stack.of(scope);
  const active = reservedConcurrency > 0;
  const broker = new lambda.Function(scope, `${id}HelloBroker`, {
    runtime: lambda.Runtime.NODEJS_24_X,
    architecture: lambda.Architecture.ARM_64,
    handler: "index.handler",
    code: lambda.Code.fromInline(
      readFileSync(new URL("hello-ingress.cjs", import.meta.url), "utf8"),
    ),
    timeout: Duration.seconds(5),
    logGroup: new logs.LogGroup(scope, `${id}HelloLogs`, {
      retention: logs.RetentionDays.ONE_MONTH,
      removalPolicy: RemovalPolicy.RETAIN,
    }),
    reservedConcurrentExecutions: reservedConcurrency,
    environment: {
      BOOTSTRAP_ACCOUNT: stack.account,
      BOOTSTRAP_PARTITION: stack.partition,
      BOOTSTRAP_ROLE_NAME: bootstrapRole.roleName,
      TRACKING_TABLE: table.tableName,
      HELLO_ENABLED: String(active),
    },
  });
  const endpoint = broker.addFunctionUrl({
    authType: lambda.FunctionUrlAuthType.AWS_IAM,
  });
  new CfnOutput(scope, `${id}HelloUrl`, {
    value: endpoint.url,
    description: `HELLO ingress for ${bootstrapRole.roleArn}`,
  });
  const dependencies = [broker, endpoint];
  if (!active) return { dependencies, statements: [] };
  if (tableKeyArn)
    broker.addToRolePolicy(
      trackingTableDecrypt(scope, tableKeyArn, table.tableName),
    );

  broker.addToRolePolicy(
    new iam.PolicyStatement({
      actions: ["dynamodb:GetItem", "dynamodb:PutItem"],
      resources: [table.tableArn],
      conditions: {
        "ForAllValues:StringLike": {
          "dynamodb:LeadingKeys": ["HELLO#BROKER#AROA*"],
        },
        Null: { "dynamodb:LeadingKeys": "false" },
      },
    }),
  );
  const statements = [
    new iam.PolicyStatement({
      actions: ["lambda:InvokeFunctionUrl"],
      resources: [broker.functionArn],
      conditions: {
        StringEquals: { "lambda:FunctionUrlAuthType": "AWS_IAM" },
      },
    }),
    new iam.PolicyStatement({
      actions: ["lambda:InvokeFunction"],
      resources: [broker.functionArn],
      conditions: { Bool: { "lambda:InvokedViaFunctionUrl": "true" } },
    }),
    new iam.PolicyStatement({
      effect: iam.Effect.DENY,
      actions: ["lambda:InvokeFunction"],
      resources: [broker.functionArn, `${broker.functionArn}:*`],
      conditions: { BoolIfExists: { "lambda:InvokedViaFunctionUrl": "false" } },
    }),
  ];
  return { dependencies, statements };
}
