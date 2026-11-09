(() => {
  const operationUrl = new URL('../api/v1/operations', document.currentScript.src);
  const navToggle = document.querySelector('[data-nav-toggle]');
  const navigation = document.querySelector('[data-navigation]');
  const setNavigationExpanded = expanded => {
    navToggle?.setAttribute('aria-expanded', String(expanded));
    navigation?.toggleAttribute('data-open', expanded);
  };
  navToggle?.addEventListener('click', () => {
    const expanded = navToggle.getAttribute('aria-expanded') === 'true';
    setNavigationExpanded(!expanded);
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && navToggle?.getAttribute('aria-expanded') === 'true') {
      setNavigationExpanded(false);
      navToggle.focus();
      event.preventDefault();
    }
  });

  const currentPath = window.location.pathname.replace(/\/$/, '');
  document.querySelectorAll('[data-navigation] a').forEach(link => {
    const linkPath = new URL(link.href).pathname.replace(/\/$/, '');
    if (currentPath === linkPath || currentPath.startsWith(linkPath + '/')) {
      link.setAttribute('aria-current', 'page');
    }
  });

  document.querySelectorAll('[data-table-filter]').forEach(input => {
    const panel = input.closest('[data-filter-panel]');
    const rows = panel?.querySelectorAll('tbody tr') ?? [];
    const state = panel?.querySelector('[data-state-filter]');
    const count = panel?.querySelector('[data-filter-count]');
    const empty = panel?.querySelector('[data-filter-empty]');
    count?.setAttribute('role', 'status');
    const apply = () => {
      const query = input.value.trim().toLocaleLowerCase();
      const selectedState = state?.value ?? '';
      let visible = 0;
      rows.forEach(row => {
        const matchesQuery = !query || row.textContent.toLocaleLowerCase().includes(query);
        const matchesState = !selectedState || row.dataset.state === selectedState;
        const show = matchesQuery && matchesState;
        row.hidden = !show;
        if (show) visible++;
      });
      if (count) count.textContent = `${visible} of ${rows.length} shown`;
      if (empty) empty.hidden = visible !== 0;
    };
    input.addEventListener('input', apply);
    state?.addEventListener('change', apply);
    apply();
  });

  document.querySelectorAll('[data-copy]').forEach(button =>
    button.addEventListener('click', async () => {
      const value = button.dataset.copy;
      if (!value) return;
      try {
        await navigator.clipboard.writeText(value);
        const original = button.textContent;
        button.textContent = 'Copied';
        window.setTimeout(() => { button.textContent = original; }, 1200);
      } catch {
        window.prompt('Copy this value:', value);
      }
    }));

  document.querySelectorAll('[data-refresh]').forEach(button =>
    button.addEventListener('click', () => window.location.reload()));

  const graphNodeWidth = 210;
  const graphNodeHeight = 52;
  const graphStageGap = 105;
  const graphNodeGap = 28;
  const graphStageDefinitions = [
    { label: 'Cluster', categories: ['kubernetes cluster'] },
    { label: 'Namespaces', categories: ['namespace'] },
    { label: 'Workloads', categories: ['deployment', 'stateful set', 'job'] },
    { label: 'Controllers', categories: ['replica set'] },
    { label: 'Runtime', categories: ['pod'] },
    { label: 'Networking', categories: ['service', 'endpoint slice'] },
    { label: 'Exposure', categories: ['ingress', 'certificate', 'external endpoint'] },
    { label: 'Images', categories: ['container image'] }
  ];
  const graphKnownStageFor = category => {
    const normalized = String(category ?? '').trim().toLocaleLowerCase();
    const index = graphStageDefinitions.findIndex(stage => stage.categories.includes(normalized));
    return index < 0 ? null : index;
  };
  const graphProperty = (element, name) =>
    (element.properties ?? []).find(property =>
      property.name.toLocaleLowerCase() === name.toLocaleLowerCase())?.value ?? '';
  const graphNodeSortKey = node =>
    `${graphProperty(node, 'namespace')}\u0000${node.category}\u0000${node.label}\u0000${node.id}`;
  const resolveGraphStages = (nodes, edges) => {
    const nodeIds = new Set(nodes.map(node => node.id));
    const predecessors = new Map(nodes.map(node => [node.id, []]));
    const successors = new Map(nodes.map(node => [node.id, []]));
    edges.forEach(edge => {
      if (!nodeIds.has(edge.sourceNodeId) || !nodeIds.has(edge.targetNodeId) ||
          edge.sourceNodeId === edge.targetNodeId) return;
      predecessors.get(edge.targetNodeId).push(edge.sourceNodeId);
      successors.get(edge.sourceNodeId).push(edge.targetNodeId);
    });
    const stages = new Map();
    nodes.forEach(node => {
      const known = graphKnownStageFor(node.category);
      if (known !== null) stages.set(node.id, known);
    });
    const unresolved = new Set(nodes.filter(node => !stages.has(node.id)).map(node => node.id));
    const hints = new Map();
    const maximumStage = Math.max(
      graphStageDefinitions.length - 1,
      Math.min(11, Math.max(2, Math.ceil(Math.sqrt(nodes.length || 1)))));
    while (unresolved.size > 0) {
      const ordered = [...unresolved].sort();
      const next = ordered.find(id =>
        predecessors.get(id).every(predecessor => !unresolved.has(predecessor))) ?? ordered[0];
      const predecessorStages = predecessors.get(next)
        .map(predecessor => stages.get(predecessor))
        .filter(stage => stage !== undefined);
      const stage = Math.min(
        maximumStage,
        Math.max(
          hints.get(next) ?? 0,
          predecessorStages.length ? Math.max(...predecessorStages) + 1 : 0));
      stages.set(next, stage);
      unresolved.delete(next);
      successors.get(next).forEach(successor => {
        if (unresolved.has(successor)) {
          hints.set(successor, Math.max(hints.get(successor) ?? 0, stage + 1));
        }
      });
    }
    return stages;
  };
  const appendText = (parent, tag, text, className) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    element.textContent = text;
    parent.append(element);
    return element;
  };
  const propertyText = properties =>
    (properties ?? []).map(property =>
      `${property.name}=${property.value ?? 'null'}`).join(' · ') || 'No properties';

  document.querySelectorAll('[data-graph-runner]').forEach(runner => {
    const brandStyles = getComputedStyle(document.documentElement);
    const graphBackground = brandStyles.getPropertyValue('--bt-deep').trim();
    const graphSurface = brandStyles.getPropertyValue('--bt-panel').trim();
    const graphLine = brandStyles.getPropertyValue('--bt-line').trim();
    const graphAccent = brandStyles.getPropertyValue('--bt-accent').trim();
    const graphText = brandStyles.getPropertyValue('--bt-ink').trim();
    const graphMuted = brandStyles.getPropertyValue('--bt-muted').trim();
    const graphHighlight = brandStyles.getPropertyValue('--bt-highlight').trim();
    const graphPalette = [
      graphAccent, '#4ade9a', '#f6c85f', '#b79cff', '#ff7f91',
      '#4fe0d4', '#ffad66', '#8fc7ff', '#c6e56f', '#f49ddd'
    ];
    const form = runner.querySelector('[data-graph-form]');
    const resultPanel = runner.querySelector('[data-graph-result]');
    const status = runner.querySelector('[data-graph-status]');
    const message = runner.querySelector('[data-graph-message]');
    const canvas = runner.querySelector('[data-graph-canvas]');
    const inspector = runner.querySelector('[data-graph-inspector]');
    const search = runner.querySelector('[data-graph-search]');
    const state = {
      result: null,
      nodes: [],
      edges: [],
      positions: new Map(),
      colors: new Map(),
      edgeColors: new Map(),
      adjacency: new Map(),
      selected: null,
      query: '',
      stages: [],
      layoutBounds: null,
      camera: { x: 0, y: 0, scale: 1 },
      drag: null
    };

    const resizeCanvas = () => {
      const rect = canvas.getBoundingClientRect();
      const ratio = Math.min(window.devicePixelRatio || 1, 2);
      const width = Math.max(320, Math.round(rect.width * ratio));
      const height = Math.max(360, Math.round(rect.height * ratio));
      if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
      }
      draw();
    };

    const layout = () => {
      state.positions.clear();
      state.stages = [];
      const groups = new Map();
      const resolvedStages = resolveGraphStages(state.nodes, state.edges);
      state.nodes.forEach(node => {
        const stage = resolvedStages.get(node.id) ?? 0;
        if (!groups.has(stage)) groups.set(stage, []);
        groups.get(stage).push(node);
      });
      const stages = [...groups.keys()].sort((left, right) => left - right);
      stages.forEach((stage, columnIndex) => {
        const nodes = groups.get(stage).sort((left, right) =>
          graphNodeSortKey(left).localeCompare(graphNodeSortKey(right)));
        const height = Math.max(
          graphNodeHeight,
          nodes.length * graphNodeHeight + Math.max(0, nodes.length - 1) * graphNodeGap);
        const x = columnIndex * (graphNodeWidth + graphStageGap);
        const categories = [...new Set(nodes.map(node => node.category))].sort();
        const knownStage = graphStageDefinitions[stage];
        const label = knownStage && categories.every(category =>
          knownStage.categories.includes(category.toLocaleLowerCase()))
          ? knownStage.label
          : categories.length <= 2
            ? categories.join(' / ')
            : `Stage ${columnIndex + 1}`;
        state.stages.push({ stage, x, label, height });
        nodes.forEach((node, index) => {
          state.positions.set(node.id, {
            x,
            y: index * (graphNodeHeight + graphNodeGap) - height / 2 + graphNodeHeight / 2
          });
        });
      });
      const positioned = [...state.positions.values()];
      state.layoutBounds = positioned.length === 0 ? null : {
        minX: Math.min(...positioned.map(point => point.x - graphNodeWidth / 2)),
        maxX: Math.max(...positioned.map(point => point.x + graphNodeWidth / 2)),
        minY: Math.min(...positioned.map(point => point.y - graphNodeHeight / 2)) - 64,
        maxY: Math.max(...positioned.map(point => point.y + graphNodeHeight / 2))
      };
    };

    const fit = () => {
      if (!state.layoutBounds) return;
      const { minX, maxX, minY, maxY } = state.layoutBounds;
      const padding = 64;
      const width = Math.max(180, maxX - minX + padding * 2);
      const height = Math.max(180, maxY - minY + padding * 2);
      state.camera.scale = Math.min(1.25, canvas.width / width, canvas.height / height);
      state.camera.x = canvas.width / 2 - ((minX + maxX) / 2) * state.camera.scale;
      state.camera.y = canvas.height / 2 - ((minY + maxY) / 2) * state.camera.scale;
      draw();
    };

    const matches = element => {
      if (!state.query) return true;
      return `${element.id} ${element.label} ${element.category} ${propertyText(element.properties)}`
        .toLocaleLowerCase().includes(state.query);
    };

    const worldToScreen = point => ({
      x: point.x * state.camera.scale + state.camera.x,
      y: point.y * state.camera.scale + state.camera.y
    });

    const pointToSegmentDistance = (point, start, end) => {
      const dx = end.x - start.x;
      const dy = end.y - start.y;
      const lengthSquared = dx * dx + dy * dy;
      if (lengthSquared === 0) return Math.hypot(point.x - start.x, point.y - start.y);
      const offset = Math.max(0, Math.min(1,
        ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared));
      return Math.hypot(
        point.x - (start.x + offset * dx),
        point.y - (start.y + offset * dy));
    };

    const bezierPoint = (geometry, progress) => {
      const remainder = 1 - progress;
      return {
        x: remainder * remainder * remainder * geometry.start.x +
          3 * remainder * remainder * progress * geometry.controlOne.x +
          3 * remainder * progress * progress * geometry.controlTwo.x +
          progress * progress * progress * geometry.end.x,
        y: remainder * remainder * remainder * geometry.start.y +
          3 * remainder * remainder * progress * geometry.controlOne.y +
          3 * remainder * progress * progress * geometry.controlTwo.y +
          progress * progress * progress * geometry.end.y
      };
    };

    const pointToBezierDistance = (point, geometry) => {
      let previous = geometry.start;
      let distance = Number.POSITIVE_INFINITY;
      for (let index = 1; index <= 24; index++) {
        const current = bezierPoint(geometry, index / 24);
        distance = Math.min(distance, pointToSegmentDistance(point, previous, current));
        previous = current;
      }
      return distance;
    };

    const clipToNode = (from, toward, width, height) => {
      const dx = toward.x - from.x;
      const dy = toward.y - from.y;
      const scale = 1 / Math.max(
        Math.abs(dx) / Math.max(1, width / 2),
        Math.abs(dy) / Math.max(1, height / 2),
        1 / Math.max(1, Math.hypot(dx, dy)));
      return { x: from.x + dx * scale, y: from.y + dy * scale };
    };

    const fitCanvasText = (context, value, maximumWidth) => {
      if (context.measureText(value).width <= maximumWidth) return value;
      let text = value;
      while (text.length > 1 && context.measureText(`${text}…`).width > maximumWidth) {
        text = text.slice(0, -1);
      }
      return `${text}…`;
    };

    const edgeGeometry = (source, target) => {
      const nodeWidth = graphNodeWidth * state.camera.scale;
      const nodeHeight = graphNodeHeight * state.camera.scale;
      const start = clipToNode(source, target, nodeWidth, nodeHeight);
      const end = clipToNode(target, source, nodeWidth, nodeHeight);
      const dx = target.x - source.x;
      const dy = target.y - source.y;
      const direction = Math.sign(dx) || 1;
      const curve = Math.max(28, Math.min(150, Math.abs(dx) * .42));
      const controlOne = Math.abs(dx) < nodeWidth
        ? { x: start.x + nodeWidth * .68, y: start.y + dy * .18 }
        : { x: start.x + direction * curve, y: start.y };
      const controlTwo = Math.abs(dx) < nodeWidth
        ? { x: end.x + nodeWidth * .68, y: end.y - dy * .18 }
        : { x: end.x - direction * curve, y: end.y };
      return { start, controlOne, controlTwo, end };
    };

    const drawArrow = (context, source, target, color, strong, directed, label) => {
      const geometry = edgeGeometry(source, target);
      const { start, controlOne, controlTwo, end } = geometry;
      context.strokeStyle = color;
      context.globalAlpha = strong ? .96 : .5;
      context.lineWidth = strong ? 2.6 : 1.35;
      context.beginPath();
      context.moveTo(start.x, start.y);
      context.bezierCurveTo(
        controlOne.x,
        controlOne.y,
        controlTwo.x,
        controlTwo.y,
        end.x,
        end.y);
      context.stroke();
      if (directed) {
        const arrowDx = end.x - controlTwo.x;
        const arrowDy = end.y - controlTwo.y;
        const arrowDistance = Math.hypot(arrowDx, arrowDy) || 1;
        const ux = arrowDx / arrowDistance;
        const uy = arrowDy / arrowDistance;
        context.fillStyle = color;
        context.beginPath();
        context.moveTo(end.x, end.y);
        context.lineTo(end.x - ux * 10 - uy * 4.5, end.y - uy * 10 + ux * 4.5);
        context.lineTo(end.x - ux * 10 + uy * 4.5, end.y - uy * 10 - ux * 4.5);
        context.closePath();
        context.fill();
      }
      if (strong && label) {
        const middle = bezierPoint(geometry, .5);
        context.globalAlpha = 1;
        context.font = '700 10px system-ui, sans-serif';
        const labelWidth = context.measureText(label).width + 12;
        context.fillStyle = graphBackground;
        context.fillRect(middle.x - labelWidth / 2, middle.y - 9, labelWidth, 17);
        context.fillStyle = graphText;
        context.textAlign = 'center';
        context.fillText(label, middle.x, middle.y + 3);
        context.textAlign = 'start';
      }
    };

    const draw = () => {
      const context = canvas.getContext('2d');
      if (!context) return;
      context.clearRect(0, 0, canvas.width, canvas.height);
      context.fillStyle = graphBackground;
      context.fillRect(0, 0, canvas.width, canvas.height);
      if (state.layoutBounds) {
        const top = worldToScreen({ x: 0, y: state.layoutBounds.minY });
        const bottom = worldToScreen({ x: 0, y: state.layoutBounds.maxY });
        state.stages.forEach((stage, index) => {
          const centre = worldToScreen({ x: stage.x, y: 0 });
          const width = (graphNodeWidth + 42) * state.camera.scale;
          context.globalAlpha = 1;
          context.fillStyle = index % 2 === 0 ? 'rgba(255,255,255,.035)' : 'rgba(255,255,255,.02)';
          context.fillRect(centre.x - width / 2, top.y, width, bottom.y - top.y);
          context.fillStyle = graphMuted;
          context.font = '800 10px system-ui, sans-serif';
          context.textAlign = 'center';
          context.fillText(stage.label.toLocaleUpperCase(), centre.x, top.y + 19);
        });
        context.textAlign = 'start';
      }
      const selectedNodeId = state.selected?.kind === 'node'
        ? state.selected.value.id
        : null;
      state.edges.forEach(edge => {
        const sourcePosition = state.positions.get(edge.sourceNodeId);
        const targetPosition = state.positions.get(edge.targetNodeId);
        if (!sourcePosition || !targetPosition) return;
        const source = worldToScreen(sourcePosition);
        const target = worldToScreen(targetPosition);
        const edgeMargin = 80;
        if (source.x < -edgeMargin && target.x < -edgeMargin ||
            source.x > canvas.width + edgeMargin && target.x > canvas.width + edgeMargin ||
            source.y < -edgeMargin && target.y < -edgeMargin ||
            source.y > canvas.height + edgeMargin && target.y > canvas.height + edgeMargin) return;
        const selected = state.selected?.kind === 'edge' && state.selected.value.id === edge.id;
        const highlighted = matches(edge);
        const incident = selectedNodeId === edge.sourceNodeId || selectedNodeId === edge.targetNodeId;
        const strong = selected || incident || highlighted && !!state.query;
        const color = selected
          ? '#ffffff'
          : state.edgeColors.get(edge.category) ?? graphAccent;
        drawArrow(context, source, target, color, strong, edge.directed, edge.category);
      });
      state.nodes.forEach(node => {
        const position = state.positions.get(node.id);
        if (!position) return;
        const point = worldToScreen(position);
        const width = graphNodeWidth * state.camera.scale;
        const height = graphNodeHeight * state.camera.scale;
        if (point.x + width / 2 < -20 || point.x - width / 2 > canvas.width + 20 ||
            point.y + height / 2 < -20 || point.y - height / 2 > canvas.height + 20) return;
        const selected = state.selected?.kind === 'node' && state.selected.value.id === node.id;
        const highlighted = matches(node);
        const connected = selectedNodeId && state.adjacency.get(selectedNodeId)?.has(node.id);
        const radius = Math.min(10, Math.max(2, 9 * state.camera.scale));
        context.globalAlpha = highlighted || !state.query || connected ? 1 : .18;
        context.fillStyle = selected ? graphHighlight : graphSurface;
        context.beginPath();
        context.roundRect(point.x - width / 2, point.y - height / 2, width, height, radius);
        context.fill();
        context.strokeStyle = selected
          ? '#ffffff'
          : connected ? graphAccent : graphLine;
        context.lineWidth = selected ? 2.8 : connected ? 2.2 : 1.2;
        context.stroke();
        context.fillStyle = state.colors.get(node.category) ?? graphPalette[0];
        context.beginPath();
        context.roundRect(
          point.x - width / 2,
          point.y - height / 2,
          Math.max(3, 7 * state.camera.scale),
          height,
          [radius, 0, 0, radius]);
        context.fill();
        if (width >= 68 || selected || highlighted && state.query) {
          context.globalAlpha = 1;
          context.fillStyle = graphText;
          const fontSize = Math.min(13, Math.max(8, 12 * state.camera.scale));
          context.font = `700 ${fontSize}px system-ui, sans-serif`;
          const textWidth = Math.max(32, width - 24 * state.camera.scale);
          const label = fitCanvasText(context, node.label, textWidth);
          context.fillText(label, point.x - width / 2 + 15 * state.camera.scale, point.y + 1);
          if (height >= 28) {
            context.fillStyle = '#88a5ba';
            context.font = `600 ${Math.max(7, fontSize - 3)}px system-ui, sans-serif`;
            context.fillText(
              fitCanvasText(context, node.category, textWidth),
              point.x - width / 2 + 15 * state.camera.scale,
              point.y + Math.min(15, 15 * state.camera.scale));
          }
        }
      });
      context.globalAlpha = 1;
    };

    const select = (kind, value) => {
      state.selected = { kind, value };
      inspector.replaceChildren();
      appendText(inspector, 'p', kind === 'node' ? 'Node' : 'Edge', 'eyebrow');
      appendText(inspector, 'h3', value.label);
      appendText(inspector, 'p', `${value.category} · ${value.id}`, 'muted');
      if (kind === 'edge') {
        appendText(inspector, 'p', `${value.sourceNodeId} ${value.directed ? '→' : '—'} ${value.targetNodeId}`);
      }
      const list = document.createElement('dl');
      list.className = 'graph-property-list';
      (value.properties ?? []).forEach(property => {
        appendText(list, 'dt', property.name);
        appendText(list, 'dd', property.value ?? 'null');
      });
      if (!value.properties?.length) appendText(inspector, 'p', 'No properties', 'muted');
      else inspector.append(list);
      draw();
    };

    const createComposition = (target, composition) => {
      target.replaceChildren();
      composition.forEach((item, index) => {
        const tag = appendText(target, 'span', `${item.category} · ${item.count}`);
        tag.style.setProperty('--category-color', graphPalette[index % graphPalette.length]);
      });
    };

    const createRows = (target, values, kind) => {
      target.replaceChildren();
      const fragment = document.createDocumentFragment();
      values.forEach(value => {
        const row = document.createElement('tr');
        row.dataset.graphText = `${value.id} ${value.label} ${value.category} ${propertyText(value.properties)}`.toLocaleLowerCase();
        const identity = document.createElement('td');
        const button = appendText(identity, 'button', value.label, 'graph-element-button');
        button.type = 'button';
        button.addEventListener('click', () => select(kind, value));
        appendText(identity, 'small', value.id);
        row.append(identity);
        if (kind === 'node') {
          appendText(row, 'td', value.category);
        } else {
          appendText(row, 'td', `${value.sourceNodeId} ${value.directed ? '→' : '—'} ${value.targetNodeId}`);
        }
        appendText(row, 'td', propertyText(value.properties));
        fragment.append(row);
      });
      target.append(fragment);
    };

    const applySearch = () => {
      state.query = search.value.trim().toLocaleLowerCase();
      runner.querySelectorAll('[data-graph-text]').forEach(row => {
        row.hidden = !!state.query && !row.dataset.graphText.includes(state.query);
      });
      draw();
    };

    const render = result => {
      state.result = result;
      state.nodes = result.nodes ?? [];
      state.edges = result.edges ?? [];
      state.adjacency.clear();
      state.nodes.forEach(node => state.adjacency.set(node.id, new Set()));
      state.edges.forEach(edge => {
        state.adjacency.get(edge.sourceNodeId)?.add(edge.targetNodeId);
        state.adjacency.get(edge.targetNodeId)?.add(edge.sourceNodeId);
      });
      state.colors.clear();
      (result.nodeComposition ?? []).forEach((item, index) =>
        state.colors.set(item.category, graphPalette[index % graphPalette.length]));
      state.edgeColors.clear();
      (result.edgeComposition ?? []).forEach((item, index) =>
        state.edgeColors.set(item.category, graphPalette[index % graphPalette.length]));
      layout();
      const durationParts = String(result.duration).split(':');
      const durationMilliseconds = durationParts.length === 3
        ? (Number(durationParts[0]) * 3600 + Number(durationParts[1]) * 60 + Number(durationParts[2])) * 1000
        : Number(result.duration) / 10000;
      const summary = runner.querySelector('[data-graph-summary]');
      summary.replaceChildren();
      [
        ['Result rows', String(result.resultRowCount)],
        ['Nodes', String(state.nodes.length)],
        ['Edges', String(state.edges.length)],
        ['Execution', `${durationMilliseconds.toFixed(2)} ms`]
      ].forEach(([label, value]) => {
        const card = document.createElement('div');
        card.className = 'card';
        appendText(card, 'span', label);
        appendText(card, 'strong', value);
        summary.append(card);
      });
      createComposition(runner.querySelector('[data-node-composition]'), result.nodeComposition ?? []);
      createComposition(runner.querySelector('[data-edge-composition]'), result.edgeComposition ?? []);
      const snapshot = runner.querySelector('[data-graph-snapshot]');
      const observedValues = state.nodes
        .map(node => graphProperty(node, 'observedAt'))
        .filter(Boolean)
        .map(value => new Date(value))
        .filter(value => !Number.isNaN(value.valueOf()));
      const storage = state.nodes.map(node => graphProperty(node, 'storage')).find(Boolean);
      const provenanceValues = state.nodes
        .map(node => graphProperty(node, 'provenance'))
        .filter(Boolean);
      const source = provenanceValues.some(value => value.includes('Live Kubernetes API'))
        ? 'Live Kubernetes API'
        : provenanceValues[0]?.split(';')[0];
      if (snapshot) {
        if (observedValues.length > 0) {
          const observedAt = new Date(Math.max(...observedValues.map(value => value.valueOf())));
          const ageSeconds = Math.max(0, Math.round((Date.now() - observedAt.valueOf()) / 1000));
          const age = ageSeconds < 60
            ? `${ageSeconds}s ago`
            : ageSeconds < 3600
              ? `${Math.round(ageSeconds / 60)}m ago`
              : `${Math.round(ageSeconds / 3600)}h ago`;
          const observedLabel = observedAt.toLocaleString(undefined, { timeZoneName: 'short' });
          snapshot.textContent = `${source ?? 'Live snapshot'} · ${age} · ${observedLabel}${storage ? ` · ${storage}` : ''}`;
          snapshot.dataset.tone = ageSeconds <= 120 ? 'fresh' : 'stale';
        } else {
          snapshot.textContent = storage
            ? `Authoritative graph result · ${storage}`
            : 'Authoritative graph result';
          snapshot.dataset.tone = 'unknown';
        }
      }
      createRows(runner.querySelector('[data-node-list]'), state.nodes, 'node');
      createRows(runner.querySelector('[data-edge-list]'), state.edges, 'edge');
      runner.querySelector('[data-node-count]').textContent = `(${state.nodes.length})`;
      runner.querySelector('[data-edge-count]').textContent = `(${state.edges.length})`;
      resultPanel.hidden = false;
      requestAnimationFrame(() => { resizeCanvas(); fit(); });
    };

    form.addEventListener('submit', async event => {
      event.preventDefault();
      const runButton = runner.querySelector('[data-graph-run]');
      const parameters = {};
      runner.querySelectorAll('[data-graph-parameter]').forEach(input => {
        parameters[input.name] = input.value;
      });
      runButton.disabled = true;
      status.textContent = 'Running';
      message.textContent = 'Executing the registered authoritative query…';
      try {
        const response = await fetch(runner.dataset.runUrl, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ parameters })
        });
        const payload = await response.json();
        if (!response.ok) throw new Error(payload.title || 'Graph execution was rejected.');
        render(payload.data);
        status.textContent = 'Complete';
        message.textContent = `Complete result loaded: ${payload.data.nodes.length} nodes and ${payload.data.edges.length} edges.`;
      } catch (error) {
        status.textContent = 'Failed';
        message.textContent = error instanceof Error ? error.message : 'Graph execution failed.';
      } finally {
        runButton.disabled = false;
      }
    });

    search?.addEventListener('input', applySearch);
    runner.querySelector('[data-graph-fit]')?.addEventListener('click', fit);
    runner.querySelector('[data-graph-reset]')?.addEventListener('click', () => {
      state.query = '';
      search.value = '';
      state.selected = null;
      applySearch();
      fit();
    });
    canvas.addEventListener('wheel', event => {
      event.preventDefault();
      const rect = canvas.getBoundingClientRect();
      const ratioX = canvas.width / rect.width;
      const ratioY = canvas.height / rect.height;
      const pointer = { x: (event.clientX - rect.left) * ratioX, y: (event.clientY - rect.top) * ratioY };
      const world = {
        x: (pointer.x - state.camera.x) / state.camera.scale,
        y: (pointer.y - state.camera.y) / state.camera.scale
      };
      const factor = Math.exp(-event.deltaY * .001);
      state.camera.scale = Math.min(4, Math.max(.08, state.camera.scale * factor));
      state.camera.x = pointer.x - world.x * state.camera.scale;
      state.camera.y = pointer.y - world.y * state.camera.scale;
      draw();
    }, { passive: false });
    canvas.addEventListener('pointerdown', event => {
      canvas.setPointerCapture(event.pointerId);
      state.drag = { x: event.clientX, y: event.clientY, moved: false };
    });
    canvas.addEventListener('pointermove', event => {
      if (!state.drag) return;
      const dx = event.clientX - state.drag.x;
      const dy = event.clientY - state.drag.y;
      if (Math.abs(dx) + Math.abs(dy) > 2) state.drag.moved = true;
      const rect = canvas.getBoundingClientRect();
      state.camera.x += dx * canvas.width / rect.width;
      state.camera.y += dy * canvas.height / rect.height;
      state.drag.x = event.clientX;
      state.drag.y = event.clientY;
      draw();
    });
    canvas.addEventListener('pointerup', event => {
      const drag = state.drag;
      state.drag = null;
      if (!drag?.moved) {
        const rect = canvas.getBoundingClientRect();
        const x = (event.clientX - rect.left) * canvas.width / rect.width;
        const y = (event.clientY - rect.top) * canvas.height / rect.height;
        const nodeWidth = graphNodeWidth * state.camera.scale;
        const nodeHeight = graphNodeHeight * state.camera.scale;
        const closestNode = state.nodes
          .map(node => ({ node, point: worldToScreen(state.positions.get(node.id)) }))
          .filter(item =>
            Math.abs(item.point.x - x) <= Math.max(10, nodeWidth / 2) &&
            Math.abs(item.point.y - y) <= Math.max(8, nodeHeight / 2))
          .map(item => ({
            ...item,
            distance: Math.hypot(item.point.x - x, item.point.y - y)
          }))
          .sort((left, right) => left.distance - right.distance)[0];
        if (closestNode) {
          select('node', closestNode.node);
          return;
        }
        const pointer = { x, y };
        const closestEdge = state.edges
          .map(edge => ({
            edge,
            source: state.positions.get(edge.sourceNodeId),
            target: state.positions.get(edge.targetNodeId)
          }))
          .filter(item => item.source && item.target)
          .map(item => ({
            edge: item.edge,
            distance: pointToBezierDistance(pointer, edgeGeometry(
              worldToScreen(item.source),
              worldToScreen(item.target)))
          }))
          .sort((left, right) => left.distance - right.distance)[0];
        if (closestEdge?.distance <= 10) select('edge', closestEdge.edge);
      }
    });
    new ResizeObserver(resizeCanvas).observe(canvas);
  });

  document.querySelectorAll('button[data-operation-kind]').forEach(button =>
    button.addEventListener('click', async () => {
      const expected = button.dataset.operationName + ':' + button.dataset.operationTarget;
      const confirmation = window.prompt('Type exactly to confirm:\n' + expected);
      if (confirmation === null) return;
      const reason = window.prompt('Reason for this operation:');
      if (!reason || !reason.trim()) return;
      const operationId = crypto.randomUUID();
      const response = await fetch(operationUrl, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-BlueTusk-Operation-Id': operationId
        },
        body: JSON.stringify({
          operationId,
          kind: Number(button.dataset.operationKind),
          target: button.dataset.operationTarget,
          confirmation,
          reason
        })
      });
      if (response.ok) window.location.reload();
      else window.alert('Operation rejected. Reference ' + operationId);
    }));
})();
