import assert from "node:assert/strict";
import { test } from "node:test";
import { App } from "aws-cdk-lib";
import { Template } from "aws-cdk-lib/assertions";
import { createSynthesisFixture } from "./fixtures/synth-app.ts";

test("the toolchain synthesizes an encrypted queue without bootstrap resources", () => {
  const app = new App();
  const stack = createSynthesisFixture(app);

  assert.deepEqual(Template.fromStack(stack).toJSON(), {
    Resources: {
      Queue: {
        Type: "AWS::SQS::Queue",
        Properties: { SqsManagedSseEnabled: true },
      },
    },
  });
});
