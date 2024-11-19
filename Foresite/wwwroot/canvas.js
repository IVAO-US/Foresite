window.getWidth = () => window.innerWidth;
window.getHeight = () => window.innerHeight;
window.getScale = () => scale;

const tau = 2 * Math.PI;
let scale = 4, isMouseDown = false, leftEdge = 0, topEdge = 0, bufferWidth = 1000, bufferHeight = 1000, lastClientX = 0, lastClientY = 0;
window.initCanvas = (bufWidth, bufHeight) => {
	const display = document.querySelector("#displayCanvas");
	display.addEventListener('wheel', we => {
		const oldScale = scale;
		scale += we.deltaY / 100; scale = Math.max(Math.min(scale, 5), 0.005);
		leftEdge -= window.innerWidth * (lastClientX / window.innerWidth) * (scale - oldScale);
		topEdge -= window.innerHeight * (lastClientY / window.innerHeight) * (scale - oldScale);
	}, { passive: true });
	display.addEventListener('mousedown', _ => { isMouseDown = true; }, { passive: true });
	display.addEventListener('mouseup', _ => { isMouseDown = false; }, { passive: true });
	display.addEventListener('mousemove', me => {
		lastClientX = me.clientX; lastClientY = me.clientY;
		if (isMouseDown === false) return;
		leftEdge -= me.movementX * scale * 0.8;
		topEdge -= me.movementY * scale * 0.8;
		console.log(`(${leftEdge}, ${topEdge})`);
	}, { passive: true });

	bufferWidth = bufWidth;
	bufferHeight = bufHeight;
	leftEdge = bufferWidth * 0.2;
	topEdge = bufferHeight * 0.65;
	requestAnimationFrame(window.draw);
}
window.draw = () => {
	const display = document.querySelector("#displayCanvas");
	const coast = document.querySelector("#coastCanvas");
	const line = document.querySelector("#lineCanvas");
	const point = document.querySelector("#pointCanvas");

	const ctx = display.getContext('2d');
	ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
	ctx.drawImage(coast, leftEdge, topEdge, window.innerWidth * scale, window.innerHeight * scale, 0, 0, window.innerWidth, window.innerHeight);
	ctx.drawImage(line, leftEdge, topEdge, window.innerWidth * scale, window.innerHeight * scale, 0, 0, window.innerWidth, window.innerHeight);
	ctx.drawImage(point, leftEdge, topEdge, window.innerWidth * scale, window.innerHeight * scale, 0, 0, window.innerWidth, window.innerHeight);

	requestAnimationFrame(window.draw);
}
window.drawCoastline = (lines) => {
	const ctx = document.querySelector("#coastCanvas").getContext('2d');
	ctx.clearRect(0, 0, bufferWidth, bufferHeight);

	ctx.strokeStyle = "white";
	let minLat = bufferHeight, minLon = bufferWidth;
	ctx.beginPath();

	for (const line of lines) {
		if (line.length < 3) continue;

		ctx.moveTo(line[0][0], line[0][1]);
		for (const point of line.slice(1)) {
			if (point[0] < minLon) minLon = point[0];
			if (point[1] < minLat) minLat = point[1];
			ctx.lineTo(point[0], point[1]);
		}
	}

	ctx.stroke();
	console.log(`COAST DRAWN: Min ${minLon}, ${minLat}`);
}
window.drawLines = (lines) => {
	const ctx = document.querySelector("#lineCanvas").getContext('2d');
	ctx.clearRect(0, 0, bufferWidth, bufferHeight);

	ctx.strokeStyle = "white";
	ctx.beginPath();

	for (const line of lines) {
		if (line.length < 3) continue;

		ctx.moveTo(line[0][0], line[0][1]);
		for (const point of line.slice(1)) {
			ctx.lineTo(point[0], point[1]);
		}
	}

	ctx.stroke();
}
window.drawPoints = (points) => {
	const ctx = document.querySelector("#pointCanvas").getContext('2d');
	ctx.clearRect(0, 0, bufferWidth, bufferHeight);

	ctx.fillStyle = "blue";
	for (const point of points) {
		const [x, y, radius] = point;
		ctx.beginPath();
		ctx.arc(x, y, radius, 0, tau);
		ctx.fill();
	}
	ctx.stroke();
}