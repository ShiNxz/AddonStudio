import { useEffect, useRef, useState } from "react";
import { Button, Tooltip } from "@heroui/react";
import { Check, Copy } from "lucide-react";
import { copyText } from "./clipboard";

interface CopyButtonProps {
  value: string;
  /** What is copied, for the tooltip and screen readers ("Copy path"). */
  label?: string;
  className?: string;
}

/** A small icon button that copies a value and shows a check for a moment. */
export function CopyButton({ value, label = "Copy", className }: CopyButtonProps) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<number | null>(null);

  useEffect(() => () => {
    if (timer.current !== null) window.clearTimeout(timer.current);
  }, []);

  const onPress = async () => {
    const ok = await copyText(value);
    if (!ok) return;
    setCopied(true);
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setCopied(false), 1400);
  };

  return (
    <Tooltip delay={500} closeDelay={0}>
      <Button
        isIconOnly
        size="sm"
        variant="ghost"
        aria-label={label}
        onPress={() => void onPress()}
        className={`shrink-0 text-muted hover:text-foreground ${className ?? ""}`}
      >
        {copied ? <Check className="text-success" /> : <Copy />}
      </Button>
      <Tooltip.Content>{copied ? "Copied" : label}</Tooltip.Content>
    </Tooltip>
  );
}
