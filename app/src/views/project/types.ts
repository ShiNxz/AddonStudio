import type { ProjectInfo } from "../../rpc/types";

export type ProjectTab = "changes" | "history" | "build" | "package" | "publish" | "settings";

/** What the Changes tab hands to Publish after a version commit (autoPublish), or a manual jump with a note. */
export interface PublishIntent {
  version: string | null;
  note: string;
  autoStart: boolean;
}

export interface ProjectViewProps {
  project: ProjectInfo;
  refreshProject: () => Promise<void>;
  goTo: (tab: ProjectTab, intent?: PublishIntent) => void;
  intent: PublishIntent | null;
}
