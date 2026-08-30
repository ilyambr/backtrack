const globalCanvas = document.createElement('canvas');
globalCanvas.width = 144;
globalCanvas.height = 144;
const globalCtx = globalCanvas.getContext('2d', { alpha: false });

function drawKeycapCanvas(ctx, options) {
  const {
    header,
    dotColor,
    iconImg,
    footer,
    footerSub,
    footerColor,
    footerFont,
    isActive,
    isSaving,
    isSaved,
    isBookmarking,
    pulsePhase,
    animBookmarkUntil,
    now
  } = options;

  ctx.fillStyle = '#000000';
  ctx.fillRect(0, 0, 144, 144);

  ctx.strokeStyle = isActive ? '#232730' : '#14161c';
  ctx.lineWidth = 2;
  ctx.strokeRect(1, 1, 142, 142);

  ctx.fillStyle = isActive ? '#FFFFFF' : '#666666';

  let headerSize = 23;
  if (header.length > 9) headerSize = 19;
  else if (header.length > 7) headerSize = 21;

  ctx.font = `900 ${headerSize}px "Segoe UI", sans-serif`;

  const maxHeaderWidth = dotColor ? 102 : 126;
  let displayHeader = header;
  if (ctx.measureText(displayHeader).width > maxHeaderWidth) {
    while (displayHeader.length > 0 && ctx.measureText(displayHeader + '...').width > maxHeaderWidth) {
      displayHeader = displayHeader.slice(0, -1);
    }
    displayHeader = displayHeader.trim() + '...';
  }

  if (dotColor) {
    ctx.textAlign = 'left';
    ctx.fillText(displayHeader, 10, 27);

    ctx.fillStyle = dotColor;
    ctx.beginPath();
    ctx.arc(127, 19, 6.5, 0, 2 * Math.PI);
    ctx.fill();
  } else {
    ctx.textAlign = 'center';
    ctx.fillText(displayHeader, 72, 27);
  }

  if (iconImg && iconImg.complete) {
    ctx.save();
    if (!isActive) {
      ctx.filter = 'grayscale(1) opacity(0.35)';
    }

    let iconSize = 58;
    let topY = 35;
    let centerY = 64;

    if (footerSub) {
      iconSize = 52;
      topY = 30;
      centerY = 56;
    } else if (!footer) {
      iconSize = 72;
      topY = 42;
      centerY = 78;
    }

    const leftX = (144 - iconSize) / 2;

    if (isSaving) {
      const pulseScale = 1.0 + 0.07 * pulsePhase;
      ctx.translate(72, centerY);
      ctx.scale(pulseScale, pulseScale);
      ctx.drawImage(iconImg, -iconSize / 2, -iconSize / 2, iconSize, iconSize);
    } else if (isBookmarking) {
      const scale = 1.0 + 0.18 * Math.sin((animBookmarkUntil - now) / 900 * Math.PI);
      ctx.translate(72, centerY);
      ctx.scale(scale, scale);
      ctx.drawImage(iconImg, -iconSize / 2, -iconSize / 2, iconSize, iconSize);
    } else {
      ctx.drawImage(iconImg, leftX, topY, iconSize, iconSize);
    }
    ctx.restore();
  }

  ctx.fillStyle = footerColor;
  ctx.font = footerFont;
  ctx.textAlign = 'center';

  if (footerSub) {
    ctx.fillText(footer, 72, 107);
    ctx.fillText(footerSub, 72, 124);
  } else if (footer) {
    ctx.fillText(footer, 72, 123);
  }

  return globalCanvas.toDataURL();
}

const touchStripCanvas = document.createElement('canvas');
touchStripCanvas.width = 200;
touchStripCanvas.height = 100;
const touchStripCtx = touchStripCanvas.getContext('2d', { alpha: false });

function drawTouchStripCanvas(ctx, options) {
  const {
    header,
    valueText,
    subText,
    iconImg,
    isActive,
    isSaving,
    isSaved,
    pulsePhase,
    accentColor
  } = options;

  const greenAccent = '#3ECF8E';

  ctx.fillStyle = '#000000';
  ctx.fillRect(0, 0, 200, 100);

  ctx.strokeStyle = isSaving ? '#f59e0b' : (isActive ? '#232730' : '#14161c');
  ctx.lineWidth = 2;
  ctx.strokeRect(1, 1, 198, 98);

  let iconSize = 36;
  let iconX = 14;
  let iconY = 16;

  if (iconImg && iconImg.complete) {
    ctx.save();
    if (!isActive) {
      ctx.filter = 'grayscale(1) opacity(0.35)';
    }

    if (isSaving) {
      const pulseScale = 1.0 + 0.12 * pulsePhase;
      ctx.translate(iconX + iconSize / 2, iconY + iconSize / 2);
      ctx.scale(pulseScale, pulseScale);
      ctx.drawImage(iconImg, -iconSize / 2, -iconSize / 2, iconSize, iconSize);
    } else {
      ctx.drawImage(iconImg, iconX, iconY, iconSize, iconSize);
    }
    ctx.restore();
  }

  ctx.fillStyle = isActive ? '#FFFFFF' : '#666666';
  ctx.font = 'bold 14px "Segoe UI", sans-serif';
  ctx.textAlign = 'left';

  let headerText = header || 'Clip Main';
  const maxHeaderWidth = 136;
  if (ctx.measureText(headerText).width > maxHeaderWidth) {
    while (headerText.length > 0 && ctx.measureText(headerText + '...').width > maxHeaderWidth) {
      headerText = headerText.slice(0, -1);
    }
    headerText += '...';
  }
  ctx.fillText(headerText, 56, 31);

  ctx.fillStyle = isActive ? (isSaved ? '#4ade80' : (isSaving ? '#f59e0b' : greenAccent)) : '#555555';
  ctx.font = '900 24px "Segoe UI", sans-serif';
  ctx.textAlign = 'left';
  ctx.fillText(valueText, 56, 56);

  ctx.fillStyle = isActive ? (isSaved ? '#4ade80' : (isSaving ? '#f59e0b' : '#94a3b8')) : '#555555';
  ctx.font = 'bold 12px "Segoe UI", sans-serif';
  ctx.textAlign = 'left';
  ctx.fillText(subText || (isActive ? 'Tap to Reset' : 'Inactive'), 14, 82);

  return touchStripCanvas.toDataURL();
}

