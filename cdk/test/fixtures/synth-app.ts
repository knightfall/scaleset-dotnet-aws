import { App, BootstraplessSynthesizer, Stack } from "aws-cdk-lib";
import { CfnQueue } from "aws-cdk-lib/aws-sqs";

export function createSynthesisFixture(app: App): Stack {
  const stack = new Stack(app, "ToolchainSmoke", {
    analyticsReporting: false,
    synthesizer: new BootstraplessSynthesizer(),
  });
  new CfnQueue(stack, "Queue", { sqsManagedSseEnabled: true });
  return stack;
}

if (import.meta.main) {
  const app = new App();
  createSynthesisFixture(app);
  app.synth();
}
