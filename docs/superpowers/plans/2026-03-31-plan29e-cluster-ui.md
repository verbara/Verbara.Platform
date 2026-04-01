# Plan 29E: Cluster UI

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a dedicated `/admin/cluster` page with node table, CRUD actions, drain management, and Platform instance visibility. Consolidate cluster info from diagnostics-page and system-page. Fix API path mismatch.

**Architecture:** New `cluster-page.tsx` with DataTable, Sheet forms, ConfirmDialogs. Rewrite `use-cluster.ts` to fix paths and add 6 new mutation hooks. Remove cluster sections from diagnostics and system pages.

**Tech Stack:** React 19, TanStack Query 5, TanStack Table, Zustand, Zod 4, React Hook Form, shadcn/ui components, TailwindCSS v4.

**Spec:** `docs/superpowers/specs/2026-03-31-v121-operations-design.md` — Sub-project D.

**Prerequisite:** Plan 29C complete (server management API endpoints live).

**Repo:** `/media/Data/Source/IPcom/Asterisk.Platform.Web/`

---

### Task 1: Rewrite use-cluster.ts

**Files:**
- Modify: `src/core/api/hooks/use-cluster.ts`

- [ ] **Step 1: Rewrite with corrected paths and all hooks**

Replace the entire file:

```typescript
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { customFetch } from '../client';
import { toast } from 'sonner';

// ── Types ────────────────────────────────────────────────────

export interface ClusterNode {
  nodeId: string;
  state: string;
  weight: number;
  priorityTier: number;
  maxCapacity: number;
  asteriskVersion?: string;
  startupTime?: string;
}

export interface DrainStatus {
  nodeId: string;
  state: string;
  startedAt: string;
  deadline: string;
  initialCallCount: number;
  remainingCallCount: number;
  naturallyCompleted: number;
  forceDisconnected: number;
  estimatedTimeToZero?: string;
}

export interface ClusterInstance {
  instanceId: string;
  lastSeen: string;
  ownedNodeIds: string[];
  totalChannels: number;
  totalAgents: number;
}

export interface ClusterStatus {
  instanceId: string;
  nodes: ClusterNode[];
  totalChannels: number;
  totalAgents: number;
  activeDrains: DrainStatus[];
  instances: ClusterInstance[];
}

export interface CreateNodeInput {
  nodeId: string;
  amiHostname: string;
  amiPort: number;
  amiUsername: string;
  amiPassword: string;
  weight?: number;
  priorityTier?: number;
  maxCapacity?: number;
  tags?: Record<string, string>;
}

export interface UpdateNodeInput {
  weight?: number;
  priorityTier?: number;
  maxCapacity?: number;
  tags?: Record<string, string>;
}

// ── Queries ──────────────────────────────────────────────────

export function useClusterStatus() {
  return useQuery({
    queryKey: ['cluster-status'],
    queryFn: () => customFetch<ClusterStatus>('/api/management/cluster/status'),
    refetchInterval: 10_000,
  });
}

export function useClusterNodes() {
  return useQuery({
    queryKey: ['cluster-nodes'],
    queryFn: () => customFetch<ClusterNode[]>('/api/management/cluster/nodes'),
    refetchInterval: 10_000,
  });
}

export function useClusterInstances() {
  return useQuery({
    queryKey: ['cluster-instances'],
    queryFn: () => customFetch<ClusterInstance[]>('/api/management/cluster/instances'),
    refetchInterval: 10_000,
  });
}

// ── Mutations ────────────────────────────────────────────────

function useInvalidateCluster() {
  const qc = useQueryClient();
  return () => {
    qc.invalidateQueries({ queryKey: ['cluster-status'] });
    qc.invalidateQueries({ queryKey: ['cluster-nodes'] });
    qc.invalidateQueries({ queryKey: ['cluster-instances'] });
  };
}

export function useCreateNode() {
  const invalidate = useInvalidateCluster();
  return useMutation({
    mutationFn: (input: CreateNodeInput) =>
      customFetch<ClusterNode>('/api/management/cluster/nodes', {
        method: 'POST',
        data: input,
      }),
    onSuccess: () => {
      invalidate();
      toast.success('Node registered');
    },
    onError: () => toast.error('Failed to register node'),
  });
}

export function useUpdateNode() {
  const invalidate = useInvalidateCluster();
  return useMutation({
    mutationFn: ({ nodeId, ...input }: UpdateNodeInput & { nodeId: string }) =>
      customFetch<ClusterNode>(`/api/management/cluster/nodes/${nodeId}`, {
        method: 'PUT',
        data: input,
      }),
    onSuccess: () => {
      invalidate();
      toast.success('Node updated');
    },
    onError: () => toast.error('Failed to update node'),
  });
}

export function useDeleteNode() {
  const invalidate = useInvalidateCluster();
  return useMutation({
    mutationFn: (nodeId: string) =>
      customFetch(`/api/management/cluster/nodes/${nodeId}`, { method: 'DELETE' }),
    onSuccess: () => {
      invalidate();
      toast.success('Node removed');
    },
    onError: () => toast.error('Failed to remove node'),
  });
}

export function useDrainNode() {
  const invalidate = useInvalidateCluster();
  return useMutation({
    mutationFn: ({ nodeId, gracePeriodSeconds }: { nodeId: string; gracePeriodSeconds?: number }) =>
      customFetch<DrainStatus>(`/api/management/cluster/nodes/${nodeId}/drain`, {
        method: 'POST',
        data: { gracePeriodSeconds },
      }),
    onSuccess: () => {
      invalidate();
      toast.success('Drain started');
    },
    onError: () => toast.error('Failed to start drain'),
  });
}

export function useCancelDrain() {
  const invalidate = useInvalidateCluster();
  return useMutation({
    mutationFn: (nodeId: string) =>
      customFetch(`/api/management/cluster/nodes/${nodeId}/drain`, { method: 'DELETE' }),
    onSuccess: () => {
      invalidate();
      toast.success('Drain cancelled');
    },
    onError: () => toast.error('Failed to cancel drain'),
  });
}

export function useForceDrain() {
  const invalidate = useInvalidateCluster();
  return useMutation({
    mutationFn: (nodeId: string) =>
      customFetch(`/api/management/cluster/nodes/${nodeId}/force-drain`, { method: 'POST' }),
    onSuccess: () => {
      invalidate();
      toast.success('Force drain completed');
    },
    onError: () => toast.error('Failed to force drain'),
  });
}
```

- [ ] **Step 2: Verify build**

Run: `cd /media/Data/Source/IPcom/Asterisk.Platform.Web && npm run build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/core/api/hooks/use-cluster.ts
git commit -m "refactor: rewrite use-cluster hooks with correct API paths and 6 new mutations"
```

---

### Task 2: Create cluster-page.tsx

**Files:**
- Create: `src/admin/cluster/cluster-page.tsx`

- [ ] **Step 1: Create the cluster page**

```tsx
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Network, Plus, Server, Cpu, Users, MoreHorizontal, Pencil, Trash2, Pause, X, Zap } from 'lucide-react';
import { PageHeader } from '../shared/page-header';
import { DataTable } from '../shared/data-table';
import { Badge } from '@/core/ui/badge';
import { Button } from '@/core/ui/button';
import { Sheet, SheetContent, SheetHeader, SheetTitle } from '@/core/ui/sheet';
import { ConfirmDeleteDialog } from '@/core/ui/confirm-delete-dialog';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from '@/core/ui/dialog';
import { Input } from '@/core/ui/input';
import { Label } from '@/core/ui/label';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/core/ui/dropdown-menu';
import {
  useClusterStatus,
  useClusterNodes,
  useClusterInstances,
  useCreateNode,
  useUpdateNode,
  useDeleteNode,
  useDrainNode,
  useCancelDrain,
  useForceDrain,
  type ClusterNode,
  type CreateNodeInput,
  type UpdateNodeInput,
} from '@/core/api/hooks/use-cluster';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import type { ColumnDef } from '@tanstack/react-table';

// ── Schemas ──────────────────────────────────────────────────

const createNodeSchema = z.object({
  nodeId: z.string().min(1, 'Required'),
  amiHostname: z.string().min(1, 'Required'),
  amiPort: z.coerce.number().int().min(1).max(65535).default(5038),
  amiUsername: z.string().min(1, 'Required'),
  amiPassword: z.string().min(1, 'Required'),
  weight: z.coerce.number().min(0).default(1.0),
  priorityTier: z.coerce.number().int().min(0).default(0),
  maxCapacity: z.coerce.number().int().min(1).default(500),
});

const editNodeSchema = z.object({
  weight: z.coerce.number().min(0),
  priorityTier: z.coerce.number().int().min(0),
  maxCapacity: z.coerce.number().int().min(1),
});

// ── State badges ─────────────────────────────────────────────

const STATE_VARIANT: Record<string, 'default' | 'secondary' | 'destructive' | 'outline'> = {
  Healthy: 'default',
  Degraded: 'outline',
  Unhealthy: 'destructive',
  Draining: 'outline',
  Offline: 'secondary',
  Unknown: 'secondary',
};

const STATE_COLOR: Record<string, string> = {
  Healthy: 'text-green-600',
  Degraded: 'text-yellow-600',
  Unhealthy: 'text-red-600',
  Draining: 'text-amber-600',
  Offline: 'text-gray-400',
  Unknown: 'text-gray-400',
};

// ── Columns ──────────────────────────────────────────────────

function useColumns(
  onEdit: (node: ClusterNode) => void,
  onDrain: (nodeId: string) => void,
  onCancelDrain: (nodeId: string) => void,
  onForceDrain: (nodeId: string) => void,
  onRemove: (nodeId: string) => void,
): ColumnDef<ClusterNode>[] {
  return [
    { accessorKey: 'nodeId', header: 'Node ID' },
    {
      accessorKey: 'state',
      header: 'State',
      cell: ({ row }) => (
        <Badge variant={STATE_VARIANT[row.original.state] ?? 'secondary'}>
          <span className={STATE_COLOR[row.original.state] ?? ''}>
            {row.original.state}
          </span>
        </Badge>
      ),
    },
    {
      id: 'channels',
      header: 'Max Capacity',
      cell: ({ row }) => `${row.original.maxCapacity}`,
    },
    { accessorKey: 'weight', header: 'Weight' },
    { accessorKey: 'priorityTier', header: 'Tier' },
    {
      accessorKey: 'asteriskVersion',
      header: 'Asterisk',
      cell: ({ row }) => row.original.asteriskVersion ?? '—',
    },
    {
      id: 'actions',
      cell: ({ row }) => {
        const node = row.original;
        const state = node.state;
        return (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="sm" data-testid={`cluster-node-${node.nodeId}-actions`}>
                <MoreHorizontal className="h-4 w-4" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              {(state === 'Healthy' || state === 'Degraded' || state === 'Offline' || state === 'Unhealthy') && (
                <DropdownMenuItem onClick={() => onEdit(node)}>
                  <Pencil className="mr-2 h-4 w-4" /> Edit
                </DropdownMenuItem>
              )}
              {(state === 'Healthy' || state === 'Degraded' || state === 'Unhealthy') && (
                <DropdownMenuItem onClick={() => onDrain(node.nodeId)}>
                  <Pause className="mr-2 h-4 w-4" /> Drain
                </DropdownMenuItem>
              )}
              {state === 'Draining' && (
                <>
                  <DropdownMenuItem onClick={() => onCancelDrain(node.nodeId)}>
                    <X className="mr-2 h-4 w-4" /> Cancel Drain
                  </DropdownMenuItem>
                  <DropdownMenuItem onClick={() => onForceDrain(node.nodeId)} className="text-destructive">
                    <Zap className="mr-2 h-4 w-4" /> Force Drain
                  </DropdownMenuItem>
                </>
              )}
              {(state === 'Offline' || state === 'Unhealthy') && (
                <DropdownMenuItem onClick={() => onRemove(node.nodeId)} className="text-destructive">
                  <Trash2 className="mr-2 h-4 w-4" /> Remove
                </DropdownMenuItem>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
        );
      },
    },
  ];
}

// ── Page ─────────────────────────────────────────────────────

export default function ClusterPage() {
  const { t } = useTranslation();
  const { data: status } = useClusterStatus();
  const { data: nodes = [] } = useClusterNodes();
  const { data: instances = [] } = useClusterInstances();

  const createNode = useCreateNode();
  const updateNode = useUpdateNode();
  const deleteNode = useDeleteNode();
  const drainNode = useDrainNode();
  const cancelDrain = useCancelDrain();
  const forceDrain = useForceDrain();

  const [showAddSheet, setShowAddSheet] = useState(false);
  const [editNode, setEditNode] = useState<ClusterNode | null>(null);
  const [drainNodeId, setDrainNodeId] = useState<string | null>(null);
  const [removeNodeId, setRemoveNodeId] = useState<string | null>(null);
  const [forceNodeId, setForceNodeId] = useState<string | null>(null);

  const healthyCount = nodes.filter((n) => n.state === 'Healthy').length;
  const totalCapacity = nodes.reduce((sum, n) => sum + n.maxCapacity, 0);

  const columns = useColumns(
    (node) => setEditNode(node),
    (id) => setDrainNodeId(id),
    (id) => cancelDrain.mutate(id),
    (id) => setForceNodeId(id),
    (id) => setRemoveNodeId(id),
  );

  return (
    <div className="space-y-6">
      <PageHeader
        title="Cluster Management"
        description="Manage Asterisk nodes and Platform API instances"
      >
        <Button onClick={() => setShowAddSheet(true)} data-testid="cluster-add-node-btn">
          <Plus className="mr-2 h-4 w-4" /> Add Node
        </Button>
      </PageHeader>

      {/* Summary Cards */}
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
        <SummaryCard icon={Server} label="Nodes" value={`${healthyCount}/${nodes.length} healthy`} testId="cluster-summary-nodes" />
        <SummaryCard icon={Cpu} label="Capacity" value={`${status?.totalChannels ?? 0}/${totalCapacity}`} testId="cluster-summary-channels" />
        <SummaryCard icon={Users} label="Agents" value={`${status?.totalAgents ?? 0}`} testId="cluster-summary-agents" />
        <SummaryCard icon={Network} label="Instances" value={`${instances.length}`} testId="cluster-summary-instances" />
      </div>

      {/* Node Table */}
      <DataTable
        columns={columns}
        data={nodes}
        searchKey="nodeId"
        searchPlaceholder="Search nodes..."
        data-testid="cluster-nodes-table"
      />

      {/* Active Drains */}
      {status?.activeDrains && status.activeDrains.length > 0 && (
        <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 p-4" data-testid="cluster-active-drains">
          <h3 className="mb-3 font-semibold text-amber-700 dark:text-amber-400">Active Drains</h3>
          {status.activeDrains.map((d) => (
            <div key={d.nodeId} className="flex items-center justify-between py-2">
              <div>
                <span className="font-mono">{d.nodeId}</span>
                <span className="ml-2 text-sm text-muted-foreground">
                  {d.remainingCallCount} remaining — {d.state}
                </span>
              </div>
              <div className="flex gap-2">
                <Button size="sm" variant="outline" onClick={() => cancelDrain.mutate(d.nodeId)}>Cancel</Button>
                <Button size="sm" variant="destructive" onClick={() => setForceNodeId(d.nodeId)}>Force</Button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Platform Instances */}
      {instances.length > 0 && (
        <div className="rounded-lg border p-4" data-testid="cluster-instances">
          <h3 className="mb-3 font-semibold">Platform Instances</h3>
          {instances.map((inst) => (
            <div key={inst.instanceId} className="flex items-center justify-between py-2 text-sm">
              <span className="font-mono">{inst.instanceId}</span>
              <span className="text-muted-foreground">
                Last seen: {new Date(inst.lastSeen).toLocaleTimeString()} — Nodes: {inst.ownedNodeIds.join(', ') || '—'} — Ch: {inst.totalChannels}
              </span>
            </div>
          ))}
        </div>
      )}

      {/* Add Node Sheet */}
      <AddNodeSheet
        open={showAddSheet}
        onOpenChange={setShowAddSheet}
        onSubmit={(input) => {
          createNode.mutate(input, { onSuccess: () => setShowAddSheet(false) });
        }}
        isPending={createNode.isPending}
      />

      {/* Edit Node Sheet */}
      {editNode && (
        <EditNodeSheet
          node={editNode}
          open={!!editNode}
          onOpenChange={(open) => { if (!open) setEditNode(null); }}
          onSubmit={(input) => {
            updateNode.mutate(
              { nodeId: editNode.nodeId, ...input },
              { onSuccess: () => setEditNode(null) },
            );
          }}
          isPending={updateNode.isPending}
        />
      )}

      {/* Drain Dialog */}
      {drainNodeId && (
        <DrainDialog
          nodeId={drainNodeId}
          open={!!drainNodeId}
          onOpenChange={(open) => { if (!open) setDrainNodeId(null); }}
          onConfirm={(gracePeriod) => {
            drainNode.mutate(
              { nodeId: drainNodeId, gracePeriodSeconds: gracePeriod },
              { onSuccess: () => setDrainNodeId(null) },
            );
          }}
          isPending={drainNode.isPending}
        />
      )}

      {/* Remove Confirmation */}
      {removeNodeId && (
        <ConfirmDeleteDialog
          title="Remove Node"
          description={`Are you sure you want to remove node "${removeNodeId}"? This action cannot be undone.`}
          open={!!removeNodeId}
          onOpenChange={(open) => { if (!open) setRemoveNodeId(null); }}
          onConfirm={() => {
            deleteNode.mutate(removeNodeId, { onSuccess: () => setRemoveNodeId(null) });
          }}
          isPending={deleteNode.isPending}
        />
      )}

      {/* Force Drain Confirmation */}
      {forceNodeId && (
        <ConfirmDeleteDialog
          title="Force Drain"
          description={`Force drain will immediately disconnect all active calls on "${forceNodeId}". This cannot be undone.`}
          open={!!forceNodeId}
          onOpenChange={(open) => { if (!open) setForceNodeId(null); }}
          onConfirm={() => {
            forceDrain.mutate(forceNodeId, { onSuccess: () => setForceNodeId(null) });
          }}
          isPending={forceDrain.isPending}
        />
      )}
    </div>
  );
}

// ── Sub-components ───────────────────────────────────────────

function SummaryCard({ icon: Icon, label, value, testId }: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  value: string;
  testId: string;
}) {
  return (
    <div className="rounded-lg border p-4" data-testid={testId}>
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <Icon className="h-4 w-4" />
        {label}
      </div>
      <div className="mt-1 text-2xl font-bold">{value}</div>
    </div>
  );
}

function AddNodeSheet({ open, onOpenChange, onSubmit, isPending }: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  onSubmit: (input: CreateNodeInput) => void;
  isPending: boolean;
}) {
  const form = useForm({
    resolver: zodResolver(createNodeSchema) as any,
    defaultValues: { nodeId: '', amiHostname: '', amiPort: 5038, amiUsername: '', amiPassword: '', weight: 1.0, priorityTier: 0, maxCapacity: 500 },
  });

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent data-testid="cluster-add-node-sheet">
        <SheetHeader><SheetTitle>Add Node</SheetTitle></SheetHeader>
        <form onSubmit={form.handleSubmit((data) => onSubmit(data as CreateNodeInput))} className="mt-4 space-y-4">
          <div><Label>Node ID</Label><Input {...form.register('nodeId')} /></div>
          <div><Label>AMI Hostname</Label><Input {...form.register('amiHostname')} /></div>
          <div><Label>AMI Port</Label><Input type="number" {...form.register('amiPort')} /></div>
          <div><Label>AMI Username</Label><Input {...form.register('amiUsername')} /></div>
          <div><Label>AMI Password</Label><Input type="password" {...form.register('amiPassword')} /></div>
          <div><Label>Weight</Label><Input type="number" step="0.1" {...form.register('weight')} /></div>
          <div><Label>Priority Tier</Label><Input type="number" {...form.register('priorityTier')} /></div>
          <div><Label>Max Capacity</Label><Input type="number" {...form.register('maxCapacity')} /></div>
          <Button type="submit" disabled={isPending} className="w-full">Add Node</Button>
        </form>
      </SheetContent>
    </Sheet>
  );
}

function EditNodeSheet({ node, open, onOpenChange, onSubmit, isPending }: {
  node: ClusterNode;
  open: boolean;
  onOpenChange: (v: boolean) => void;
  onSubmit: (input: UpdateNodeInput) => void;
  isPending: boolean;
}) {
  const form = useForm({
    resolver: zodResolver(editNodeSchema) as any,
    defaultValues: { weight: node.weight, priorityTier: node.priorityTier, maxCapacity: node.maxCapacity },
  });

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent data-testid="cluster-edit-node-sheet">
        <SheetHeader><SheetTitle>Edit {node.nodeId}</SheetTitle></SheetHeader>
        <form onSubmit={form.handleSubmit((data) => onSubmit(data))} className="mt-4 space-y-4">
          <div><Label>Weight</Label><Input type="number" step="0.1" {...form.register('weight')} /></div>
          <div><Label>Priority Tier</Label><Input type="number" {...form.register('priorityTier')} /></div>
          <div><Label>Max Capacity</Label><Input type="number" {...form.register('maxCapacity')} /></div>
          <Button type="submit" disabled={isPending} className="w-full">Update Node</Button>
        </form>
      </SheetContent>
    </Sheet>
  );
}

function DrainDialog({ nodeId, open, onOpenChange, onConfirm, isPending }: {
  nodeId: string;
  open: boolean;
  onOpenChange: (v: boolean) => void;
  onConfirm: (gracePeriod?: number) => void;
  isPending: boolean;
}) {
  const [gracePeriod, setGracePeriod] = useState(600);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Drain Node: {nodeId}</DialogTitle>
        </DialogHeader>
        <div className="space-y-4 py-4">
          <p className="text-sm text-muted-foreground">
            Draining will stop new calls from being routed to this node. Existing calls will complete naturally or be force-disconnected after the grace period.
          </p>
          <div>
            <Label>Grace Period (seconds)</Label>
            <Input
              type="number"
              value={gracePeriod}
              onChange={(e) => setGracePeriod(Number(e.target.value))}
              min={0}
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button onClick={() => onConfirm(gracePeriod)} disabled={isPending}>Start Drain</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
```

- [ ] **Step 2: Verify build**

Run: `npm run build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/admin/cluster/cluster-page.tsx
git commit -m "feat: add cluster management page with node table, CRUD, drain actions, instances"
```

---

### Task 3: Add route and sidebar entry

**Files:**
- Modify: `src/router.tsx`
- Modify: `src/admin/sidebar.tsx`

- [ ] **Step 1: Add lazy import and route in router.tsx**

Add lazy import near other admin imports:
```typescript
const ClusterPage = lazy(() => import('./admin/cluster/cluster-page'));
```

Add route in the admin children, after the diagnostics route:
```typescript
{
  path: 'cluster',
  element: (
    <PermissionGuard requires="platform:cluster:manage">
      <ClusterPage />
    </PermissionGuard>
  ),
},
```

- [ ] **Step 2: Add sidebar item in sidebar.tsx**

Import `Network` icon:
```typescript
import { ..., Network } from 'lucide-react';
```

In the system group items array, add after the diagnostics entry:
```typescript
{ key: 'cluster', path: '/admin/cluster', icon: Network, label: 'Cluster' },
```

- [ ] **Step 3: Verify build**

Run: `npm run build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/router.tsx src/admin/sidebar.tsx
git commit -m "feat: add cluster route and sidebar entry with Network icon"
```

---

### Task 4: Consolidate — remove cluster from diagnostics and system pages

**Files:**
- Modify: `src/admin/system/diagnostics-page.tsx`
- Modify: `src/admin/system/system-page.tsx`

- [ ] **Step 1: Remove cluster sections from diagnostics-page.tsx**

Remove:
- The Cluster Nodes table section (the `<table>` with node rows)
- The Active Drains section (amber-styled warning)
- Keep: Platform Info card, License card, Cluster summary card (nodeCount, totalChannels, totalAgents — just the status card, not the detailed table)

Replace the detailed table with a link to the cluster page:
```tsx
<p className="mt-2 text-sm text-muted-foreground">
  <a href="/admin/cluster" className="text-primary underline">Manage cluster →</a>
</p>
```

- [ ] **Step 2: Remove cluster sections from system-page.tsx**

Remove:
- Node cards with drain buttons (the grid of node status cards)
- Any cluster-specific state (NODE_STATE_STYLES if only used for node cards)
- Keep: System settings form (timezone, language, platform name)

- [ ] **Step 3: Verify build**

Run: `npm run build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/admin/system/diagnostics-page.tsx src/admin/system/system-page.tsx
git commit -m "refactor: remove cluster detail sections from diagnostics and system pages (moved to /admin/cluster)"
```
