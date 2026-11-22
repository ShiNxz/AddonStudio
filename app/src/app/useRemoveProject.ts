// Removes a project from the recent list (projects.remove never deletes files), after a confirm.
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { call } from "../rpc/client";
import { confirm } from "../components/confirm";
import { notifySuccess } from "../components/notify";
import { keys } from "./queries";

export function useRemoveProject(onRemoved: () => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ path, title }: { path: string; title: string }) => {
      const ok = await confirm({
        title: `Remove ${title} from the list?`,
        body: "The folder and its files stay where they are. You can add it again later.",
        confirmLabel: "Remove",
      });
      if (!ok) return null;
      return call("projects.remove", { path });
    },
    onSuccess: (list) => {
      if (!list) return;
      queryClient.setQueryData(keys.projects, list);
      notifySuccess("Removed from the list");
      onRemoved();
    },
  });
}
